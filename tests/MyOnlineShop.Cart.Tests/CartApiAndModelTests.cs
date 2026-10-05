using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Cart.Infrastructure.Persistence;
using Xunit;
using CartAggregate = MyOnlineShop.Cart.Domain.Cart;

namespace MyOnlineShop.Cart.Tests;

internal sealed class OfflineConnectionRequested : Exception;
internal sealed class BlockDatabaseConnection : DbConnectionInterceptor
{
    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
        ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
        throw new OfflineConnectionRequested();
}
public sealed class CartApiAndModelTests
{
    [Fact]
    public async Task API_uses_authenticated_owner_ignores_client_authority_and_preserves_response_contracts()
    {
        using var factory = new CartApiFactory(); var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        using var anonymous = factory.Client(); using var first = factory.Client(owner); using var second = factory.Client(other);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/cart")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/cart/items", new { Quantity = 1 })).StatusCode);
        first.DefaultRequestHeaders.Add("X-Correlation-ID", "cart-api-test");
        using var added = await first.PostAsJsonAsync("/api/v1/cart/items", new
        { ProductVariantId = factory.Catalog.Id, Quantity = 2, UserId = other, Price = 0.01, ProductName = "Client fake", Total = 0.02 });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var response = (await added.Content.ReadFromJsonAsync<ApiResponse<CartDto>>())!;
        Assert.Equal("cart-api-test", response.CorrelationId); var item = Assert.Single(response.Data.Items);
        Assert.DoesNotContain("price", await added.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var otherCart = await second.GetAsync($"/api/v1/cart?userId={owner}");
        Assert.Empty((await otherCart.Content.ReadFromJsonAsync<ApiResponse<CartDto>>())!.Data.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await second.PutAsJsonAsync($"/api/v1/cart/items/{item.Id}", new { Quantity = 8 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await second.DeleteAsync($"/api/v1/cart/items/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await second.DeleteAsync($"/api/v1/cart?userId={owner}")).StatusCode);
        using var updated = await first.PutAsJsonAsync($"/api/v1/cart/items/{item.Id}", new { Quantity = 4 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(4, Assert.Single((await updated.Content.ReadFromJsonAsync<ApiResponse<CartDto>>())!.Data.Items).Quantity);
        using var invalid = await first.PutAsJsonAsync($"/api/v1/cart/items/{item.Id}", new { Quantity = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType!.MediaType);
        Assert.Contains("cart-api-test", await invalid.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await first.DeleteAsync($"/api/v1/cart/items/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await first.DeleteAsync("/api/v1/cart")).StatusCode);
    }
    [Fact]
    public async Task Empty_or_invalid_authenticated_subject_cannot_access_cart()
    {
        using var factory = new CartApiFactory(); using var client = factory.Client(Guid.Empty);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/cart")).StatusCode);
    }
    [Fact]
    public async Task SQL_model_migration_and_read_projection_are_verified_without_database_access()
    {
        using var context = new CartDbContext(new DbContextOptionsBuilder<CartDbContext>()
            .UseSqlServer("Server=localhost;Database=CartOffline;Integrated Security=True")
            .AddInterceptors(new BlockDatabaseConnection()).Options);
        Assert.Single(context.Database.GetMigrations());
        Assert.All(context.Model.GetEntityTypes(), value => Assert.Equal("cart", value.GetSchema()));
        var sql = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Carts_UserId]", sql); Assert.Contains("[IsActive] = 1", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Items_CartId_ProductVariantId]", sql);
        Assert.Contains("[Quantity] >= 1 AND [Quantity] <= 999", sql);
        Assert.DoesNotContain("DROP TABLE", sql); Assert.DoesNotContain("[inventory]", sql);
        Assert.DoesNotContain("Price", sql); Assert.DoesNotContain("Reserved", sql);
        Assert.Contains(context.Model.FindEntityType(typeof(CartAggregate))!.GetProperties(), property => property.IsConcurrencyToken);
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => new CartReadStore(context).GetCurrentAsync(Guid.NewGuid(), default));
    }
}
