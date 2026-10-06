using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Cart.Infrastructure;
using MyOnlineShop.Cart.Infrastructure.Persistence;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Discount.Infrastructure;
using MyOnlineShop.Discount.Infrastructure.Persistence;
using MyOnlineShop.Inventory.Infrastructure;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using MyOnlineShop.Order.Application;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Order.Domain;
using MyOnlineShop.Order.Infrastructure;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Infrastructure;
using MyOnlineShop.Pricing.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Order.Tests;

public sealed class OrderSqlFactAttribute : FactAttribute
{
    public OrderSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORDER_TEST_SQL_SERVER")))
            Skip = "SQL Server unavailable. Set ORDER_TEST_SQL_SERVER only for a future authorized disposable SQL Server test run.";
    }
}
public sealed class OrderSqlFixture : IAsyncLifetime
{
    private readonly string? _server = Environment.GetEnvironmentVariable("ORDER_TEST_SQL_SERVER");
    private readonly string _database = "MyOnlineShop_OrderTests_" + Guid.NewGuid().ToString("N");
    private bool _created;
    public ServiceProvider Provider { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";
    internal CatalogReferences Catalog { get; } = new();
    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_server)) return;
        var options = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" };
        if (options.DataSource.Contains("localdb", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("LocalDB is prohibited.");
        await using var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync(); _created = true; options.InitialCatalog = _database; ConnectionString = options.ConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:SqlServer"] = ConnectionString }).Build();
        var services = new ServiceCollection().AddLogging(); services.AddFoundationInfrastructure(configuration);
        services.AddCartInfrastructure(configuration); services.AddPricingInfrastructure(configuration); services.AddDiscountInfrastructure(configuration);
        services.AddInventoryInfrastructure(configuration); services.AddOrderInfrastructure(configuration);
        services.AddSingleton<ICatalogVariantReferences>(Catalog); services.AddSingleton<TimeProvider, FixedClock>(); services.AddScoped<IRequestContext, RequestContext>();
        Provider = services.BuildServiceProvider();
        using var scope = Provider.CreateScope(); var provider = scope.ServiceProvider;
        foreach (var db in new DbContext[] { provider.GetRequiredService<CartDbContext>(), provider.GetRequiredService<PricingDbContext>(),
            provider.GetRequiredService<DiscountDbContext>(), provider.GetRequiredService<InventoryDbContext>(), provider.GetRequiredService<OrderDbContext>() })
            await db.Database.MigrateAsync();
        await MemoryModules.SeedAsync(provider, Catalog, second: true);
    }
    public async Task<CartDto> Add(Guid user, int quantity = 2)
    { using var scope = Provider.CreateScope(); return await scope.ServiceProvider.GetRequiredService<ICartCommands>().AddAsync(user, new() { ProductVariantId = Catalog.Id, Quantity = quantity }, default); }
    public async Task<OrderDto> Checkout(Guid user, Guid key)
    { using var scope = Provider.CreateScope(); return await scope.ServiceProvider.GetRequiredService<ICheckoutCommands>().CreateAsync(user, new() { IdempotencyKey = key, Currency = "IRR" }, default); }
    public async Task DisposeAsync()
    {
        if (Provider is not null) await Provider.DisposeAsync();
        if (!_created) return;
        const string prefix = "MyOnlineShop_OrderTests_";
        if (!_database.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(_database[prefix.Length..], "N", out _))
            throw new InvalidOperationException("Unsafe test database cleanup target.");
        SqlConnection.ClearAllPools();
        var options = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync();
    }
}
public sealed class SqlTests(OrderSqlFixture fixture) : IClassFixture<OrderSqlFixture>
{
    [OrderSqlFact]
    public async Task Checkout_persists_snapshot_clears_cart_and_keeps_stock_five_and_movements_unchanged()
    {
        var user = Guid.NewGuid(); await fixture.Add(user);
        var order = await fixture.Checkout(user, Guid.NewGuid()); Assert.Equal(2, order.Items.Single().Quantity);
        Assert.Equal(1_000_000m, order.Items.Single().UnitPrice); Assert.Equal(2_000_000m, order.PayableAmount);
        using var scope = fixture.Provider.CreateScope(); var provider = scope.ServiceProvider;
        var stored = await provider.GetRequiredService<IOrderQueries>().GetMyAsync(user, order.Id, default);
        Assert.Equal(order.Id, stored.Id); Assert.Equal("Original Product", stored.Items.Single().ProductName);
        Assert.Empty((await provider.GetRequiredService<ICartQueries>().GetCurrentAsync(user, default)).Items);
        var inventory = provider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(5, (await inventory.Stocks.AsNoTracking().SingleAsync(value => value.ProductVariantId == fixture.Catalog.Id)).Quantity);
        Assert.Empty(await inventory.Movements.ToListAsync());
    }
    [OrderSqlFact]
    public async Task Concurrent_same_key_creates_one_order_and_replay_does_not_clear_new_cart()
    {
        var user = Guid.NewGuid(); var key = Guid.NewGuid(); await fixture.Add(user);
        var results = await Task.WhenAll(fixture.Checkout(user, key), fixture.Checkout(user, key));
        Assert.Equal(results[0].Id, results[1].Id);
        await fixture.Add(user, 1); Assert.Equal(results[0].Id, (await fixture.Checkout(user, key)).Id);
        using var scope = fixture.Provider.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.CountAsync(value => value.UserId == user));
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<ICartQueries>().GetCurrentAsync(user, default)).Items.Single().Quantity);
    }
    [OrderSqlFact]
    public async Task Failure_after_cart_flush_rolls_back_both_order_and_cart_without_distributed_transaction()
    {
        var user = Guid.NewGuid(); var original = await fixture.Add(user);
        using var scope = fixture.Provider.CreateScope(); var provider = scope.ServiceProvider;
        var cart = provider.GetRequiredService<ICheckoutCart>(); var db = provider.GetRequiredService<OrderDbContext>();
        Guid insertedId = Guid.Empty;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<IOrderUnitOfWork>().ExecuteAsync<int>(true, async ct =>
        {
            var snapshot = await cart.GetAsync(user, ct);
            await cart.ClearAsync(user, original.Id!.Value, snapshot.Revision, ct);
            var order = OrderTests.Create(); insertedId = order.Id; db.Orders.Add(order); await db.SaveChangesAsync(ct);
            Assert.Same(db.Database.GetDbConnection(), provider.GetRequiredService<CartDbContext>().Database.GetDbConnection());
            Assert.Equal(Guid.Empty, System.Transactions.Transaction.Current?.TransactionInformation.DistributedIdentifier ?? Guid.Empty);
            throw new InvalidOperationException("Injected failure after both module flushes.");
        }, default));
        db.ChangeTracker.Clear(); Assert.False(await db.Orders.AnyAsync(value => value.Id == insertedId));
        var restored = await provider.GetRequiredService<ICartQueries>().GetCurrentAsync(user, default);
        Assert.Equal(2, restored.Items.Single().Quantity);
    }
    [OrderSqlFact]
    public async Task Cart_change_during_checkout_is_detected_and_new_cart_content_is_preserved()
    {
        var user = Guid.NewGuid(); var cart = await fixture.Add(user);
        using var scope = fixture.Provider.CreateScope(); var provider = scope.ServiceProvider;
        var original = await provider.GetRequiredService<ICheckoutCart>().GetAsync(user, default);
        await fixture.Add(user, 1);
        await Assert.ThrowsAnyAsync<Exception>(() => provider.GetRequiredService<IOrderUnitOfWork>().ExecuteAsync(true, async ct =>
        { await provider.GetRequiredService<ICheckoutCart>().ClearAsync(user, cart.Id!.Value, original.Revision, ct); return true; }, default));
        Assert.Equal(3, (await provider.GetRequiredService<ICartQueries>().GetCurrentAsync(user, default)).Items.Single().Quantity);
        Assert.False(await provider.GetRequiredService<OrderDbContext>().Orders.AnyAsync(value => value.UserId == user));
    }
    [OrderSqlFact]
    public async Task Unique_keys_owner_reads_status_audit_and_rowversion_are_enforced_by_SQL()
    {
        var user = Guid.NewGuid(); await fixture.Add(user); var order = await fixture.Checkout(user, Guid.NewGuid());
        using var first = fixture.Provider.CreateScope(); using var second = fixture.Provider.CreateScope();
        var queries = first.ServiceProvider.GetRequiredService<IOrderQueries>();
        await Assert.ThrowsAsync<OrderException>(() => queries.GetMyAsync(Guid.NewGuid(), order.Id, default));
        var db = second.ServiceProvider.GetRequiredService<OrderDbContext>(); var stale = await db.Orders.SingleAsync(value => value.Id == order.Id);
        Assert.Equal("Cancelled", (await first.ServiceProvider.GetRequiredService<IOrderCommands>().CancelAsync(user, order.Id, default)).Status);
        stale.Transition(OrderStatus.Failed, FixedClock.Now);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear(); Assert.Equal(2, await db.Audit.CountAsync(value => value.OrderId == order.Id));
        var stored = await db.Orders.SingleAsync(value => value.Id == order.Id);
        var duplicate = OrderTests.Create();
        // Test fixture writes an existing unique key without exposing a mutation API.
        db.Orders.Add(duplicate);
        db.Entry(duplicate).Property(value => value.UserId).CurrentValue = stored.UserId;
        db.Entry(duplicate).Property(value => value.IdempotencyKey).CurrentValue = stored.IdempotencyKey;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
