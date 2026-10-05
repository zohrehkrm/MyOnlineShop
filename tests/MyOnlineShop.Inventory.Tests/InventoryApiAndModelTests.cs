using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Infrastructure;
using MyOnlineShop.Catalog.Infrastructure.Persistence;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Infrastructure.Persistence;
using MyOnlineShop.Inventory.Application;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Inventory.Domain;
using MyOnlineShop.Inventory.Infrastructure;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Inventory.Tests;

internal sealed class OfflineConnectionRequested : Exception;
internal sealed class BlockDatabaseConnection : DbConnectionInterceptor
{
    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
        ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
        throw new OfflineConnectionRequested();
}
public sealed class InventoryApiFactory : WebApplicationFactory<Program>
{
    internal MemoryStore Store { get; } = new();
    internal VariantReferences Catalog { get; } = new();
    public Guid Actor { get; } = Guid.NewGuid();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:SqlServer", "Server=localhost;Database=InventoryOffline;Integrated Security=True");
        builder.UseSetting("IdentitySecurity:SigningKeyBase64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IInventoryStore>(); services.AddSingleton<IInventoryStore>(Store);
            services.RemoveAll<IInventoryUnitOfWork>(); services.AddSingleton<IInventoryUnitOfWork>(Store);
            services.RemoveAll<ICatalogVariantReferences>(); services.AddSingleton<ICatalogVariantReferences>(Catalog);
            services.AddSingleton(new TestActor(Actor));
            services.AddAuthentication(options =>
            { options.DefaultAuthenticateScheme = "InventoryTest"; options.DefaultChallengeScheme = "InventoryTest"; options.DefaultForbidScheme = "InventoryTest"; })
                .AddScheme<AuthenticationSchemeOptions, InventoryAuthentication>("InventoryTest", _ => { });
        });
    }
    public HttpClient Client(string? permission = null)
    {
        var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        if (permission is not null) client.DefaultRequestHeaders.Add("X-Test-Permission", permission);
        return client;
    }
}
internal sealed record TestActor(Guid Id);
internal sealed class InventoryAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, TestActor actor) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var permission = Request.Headers["X-Test-Permission"].ToString();
        if (permission.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new("sub", actor.Id.ToString()), new("permission", permission)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
public sealed class InventoryApiAndModelTests
{
    [Fact]
    public async Task API_enforces_permissions_validation_correlation_and_server_actor()
    {
        using var factory = new InventoryApiFactory();
        using var anonymous = factory.Client(); using var reader = factory.Client(IdentityPermissions.ViewInventory);
        using var adjuster = factory.Client(IdentityPermissions.AdjustInventory);
        using var receiver = factory.Client(IdentityPermissions.ReceiveInventory);
        var input = new WarehouseInput { Name = "Main", Code = "main" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/inventory/warehouses", input)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/inventory/warehouses", input)).StatusCode);
        using var created = await adjuster.PostAsJsonAsync("/api/v1/inventory/warehouses", input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.NotNull(created.Headers.Location);
        var warehouse = (await created.Content.ReadFromJsonAsync<ApiResponse<WarehouseDto>>())!.Data;
        receiver.DefaultRequestHeaders.Add("X-Correlation-ID", "inventory-api-test");
        var receipt = new { OperationId = Guid.NewGuid(), WarehouseId = warehouse.Id, ProductVariantId = factory.Catalog.Id,
            Quantity = 2, Reference = "receipt-1", Reason = "Received goods", ActorId = Guid.NewGuid() };
        using var received = await receiver.PostAsJsonAsync("/api/v1/inventory/receipts", receipt);
        Assert.Equal(HttpStatusCode.OK, received.StatusCode);
        var dto = (await received.Content.ReadFromJsonAsync<ApiResponse<MovementDto>>())!;
        Assert.Equal(factory.Actor, dto.Data.ActorId); Assert.Equal("inventory-api-test", dto.CorrelationId);
        Assert.Equal(dto.CorrelationId, dto.Data.CorrelationId);
        using var replayed = await receiver.PostAsJsonAsync("/api/v1/inventory/receipts", receipt);
        Assert.Equal(dto.Data.Id, (await replayed.Content.ReadFromJsonAsync<ApiResponse<MovementDto>>())!.Data.Id);
        using var invalid = await receiver.PostAsJsonAsync("/api/v1/inventory/receipts", new { Quantity = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/inventory/receipts", receipt)).StatusCode);
        Assert.Single(factory.Store.Movements);
    }
    [Fact]
    public async Task All_inventory_policies_require_assigned_permission_instead_of_role_name()
    {
        using var factory = new InventoryApiFactory();
        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();
        foreach (var permission in new[] { IdentityPermissions.ViewInventory, IdentityPermissions.ReceiveInventory,
                     IdentityPermissions.AdjustInventory, IdentityPermissions.DeductInventory })
        {
            var roleOnly = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrator")], "Test"));
            var permitted = new ClaimsPrincipal(new ClaimsIdentity([new Claim("permission", permission)], "Test"));
            Assert.False((await authorization.AuthorizeAsync(roleOnly, null, permission)).Succeeded);
            Assert.True((await authorization.AuthorizeAsync(permitted, null, permission)).Succeeded);
        }
    }
    [Fact]
    public void Offline_model_and_migrations_enforce_inventory_owned_keys_and_nonnegative_stock()
    {
        using var context = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlServer("Server=localhost;Database=InventoryOffline;Integrated Security=True").Options);
        Assert.Single(context.Database.GetMigrations());
        Assert.All(context.Model.GetEntityTypes(), entity => Assert.Equal("inventory", entity.GetSchema()));
        var sql = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Stocks_WarehouseId_ProductVariantId]", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Movements_OperationId]", sql);
        Assert.Contains("[Quantity] >= 0", sql); Assert.DoesNotContain("DROP TABLE", sql);
        Assert.DoesNotContain("REFERENCES [catalog]", sql);
        Assert.Contains(context.Model.FindEntityType(typeof(Stock))!.GetProperties(), property => property.IsConcurrencyToken);
        using var identity = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlServer("Server=localhost;Database=IdentityOffline;Integrated Security=True").Options);
        var migrations = identity.Database.GetMigrations().ToArray();
        var previous = migrations.Single(value => value.EndsWith("_AddCatalogPermission", StringComparison.Ordinal));
        var latest = migrations.Single(value => value.EndsWith("_AddInventoryPermissions", StringComparison.Ordinal));
        var permissionSql = identity.GetService<IMigrator>().GenerateScript(previous, latest);
        Assert.Contains("inventory.deduct", permissionSql); Assert.Contains("inventory.receive", permissionSql);
        Assert.Contains("inventory.adjust", permissionSql); Assert.Contains("inventory.view", permissionSql);
        Assert.DoesNotContain("CREATE TABLE", permissionSql);
    }
    [Fact]
    public async Task SQL_query_translation_is_checked_without_opening_a_database_connection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:SqlServer"] = "Server=localhost;Database=InventoryOffline;Integrated Security=True" }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddFoundationInfrastructure(configuration); services.AddCatalogInfrastructure(configuration);
        services.AddInventoryInfrastructure(configuration);
        var block = new BlockDatabaseConnection();
        services.AddDbContext<InventoryDbContext>(options => options.AddInterceptors(block));
        services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(block));
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IInventoryQueries>();
        var id = Guid.NewGuid();
        var read = (InventoryReadStore)scope.ServiceProvider.GetRequiredService<IInventoryReadStore>();
        Assert.Contains("SELECT", read.StockPage(new() { WarehouseId = id, LowStockOnly = true }).ToQueryString());
        Assert.Contains("SELECT", read.MovementPage(new() { WarehouseId = id, MovementType = "Sale" }).ToQueryString());
        Assert.Contains("SELECT", read.ReceiptPage(new() { WarehouseId = id }).ToQueryString());
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => query.GetWarehouseAsync(id, default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => query.ListWarehousesAsync(new() { IsActive = true }, default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => query.GetStockAsync(id, id, default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => query.ListStockAsync(new() { WarehouseId = id, LowStockOnly = true }, default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => query.ListMovementsAsync(new() { WarehouseId = id, MovementType = "Sale" }, default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => query.ListReceiptsAsync(new() { WarehouseId = id }, default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => scope.ServiceProvider.GetRequiredService<ICatalogVariantReferences>().GetAsync(id, default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => scope.ServiceProvider.GetRequiredService<IInventoryStore>().FindOperationAsync(id, default));
    }
}
