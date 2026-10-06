using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MyOnlineShop.Cart.Infrastructure.Persistence;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using MyOnlineShop.Order.Domain;
using MyOnlineShop.Order.Infrastructure.Persistence;
using Xunit;
using OrderAggregate = MyOnlineShop.Order.Domain.Order;

namespace MyOnlineShop.Order.Tests;

internal sealed class OfflineConnectionRequested : Exception;
internal sealed class BlockConnection : DbConnectionInterceptor
{
    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData data,
        InterceptionResult result, CancellationToken ct = default) => throw new OfflineConnectionRequested();
}
public sealed class ModelTests
{
    [Fact]
    public async Task SQL_model_migration_and_projection_translate_without_opening_database()
    {
        const string connection = "Server=localhost;Database=OrderOffline;Integrated Security=True";
        using var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseSqlServer(connection).AddInterceptors(new BlockConnection()).Options);
        Assert.Single(db.Database.GetMigrations()); Assert.All(db.Model.GetEntityTypes(), entity => Assert.Equal("ordering", entity.GetSchema()));
        var sql = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("decimal(18,4)", sql); Assert.Contains("rowversion", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Orders_UserId_IdempotencyKey]", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Orders_CartId_CartRevision]", sql); Assert.DoesNotContain("DROP TABLE", sql);
        Assert.All(db.Model.GetEntityTypes().SelectMany(value => value.GetForeignKeys()), key => Assert.Equal("ordering", key.PrincipalEntityType.GetSchema()));
        var queries = new OrderReadStore(db);
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => queries.GetMyAsync(Guid.NewGuid(), Guid.NewGuid(), default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => queries.GetAsync(Guid.NewGuid(), default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => queries.ListMyAsync(Guid.NewGuid(), 1, 20, default));
        using var inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseSqlServer(connection).AddInterceptors(new BlockConnection()).Options);
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => new InventoryAvailability(inventory).GetAsync([Guid.NewGuid()], default));
        using var cart = new CartDbContext(new DbContextOptionsBuilder<CartDbContext>().UseSqlServer(connection).AddInterceptors(new BlockConnection()).Options);
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => new CheckoutCart(cart, new CartRepository(cart), new FixedClock()).GetAsync(Guid.NewGuid(), default));
    }
    [Fact]
    public void Order_has_no_other_module_internal_or_payment_dependencies()
    {
        var application = typeof(MyOnlineShop.Order.Application.CheckoutCommands).Assembly;
        Assert.DoesNotContain(application.GetReferencedAssemblies(), value => value.Name!.Contains("Infrastructure") || value.Name.Contains("Presentation") ||
            value.Name.Contains("Payment") || value.Name.Contains("Wallet") || value.Name.Contains("Shipping"));
        Assert.DoesNotContain(typeof(OrderAggregate).GetProperties(), property => property.PropertyType == typeof(float) || property.PropertyType == typeof(double));
        Assert.DoesNotContain(typeof(OrderItem).GetProperties(), property => property.PropertyType == typeof(float) || property.PropertyType == typeof(double));
    }
}
