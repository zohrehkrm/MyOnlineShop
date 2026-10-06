using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.Order.Domain;
using MyOnlineShop.Order.Infrastructure;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Shipping.Application;
using MyOnlineShop.Shipping.Contracts;
using MyOnlineShop.Shipping.Domain;
using MyOnlineShop.Shipping.Infrastructure;
using MyOnlineShop.Shipping.Infrastructure.Persistence;
using Xunit;
using OrderAggregate = MyOnlineShop.Order.Domain.Order;

namespace MyOnlineShop.Shipping.Tests;

public sealed class ShippingSqlFactAttribute : FactAttribute
{
    public ShippingSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SHIPPING_TEST_SQL_SERVER")))
            Skip = "SQL Server unavailable. SHIPPING_TEST_SQL_SERVER requires a future authorized disposable-database test run; LocalDB is prohibited.";
    }
}
public sealed class ShippingSqlFixture : IAsyncLifetime
{
    private readonly string? _server = Environment.GetEnvironmentVariable("SHIPPING_TEST_SQL_SERVER");
    private readonly string _database = "MyOnlineShop_ShippingTests_" + Guid.NewGuid().ToString("N");
    private bool _created;
    public ServiceProvider Provider { get; private set; } = null!;
    public Guid Actor { get; } = Guid.NewGuid();
    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_server)) return;
        var settings = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master", MultipleActiveResultSets = false };
        if (settings.DataSource.Contains("localdb", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("LocalDB prohibited.");
        await using var connection = new SqlConnection(settings.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync(); _created = true; settings.InitialCatalog = _database;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:SqlServer"] = settings.ConnectionString }).Build();
        var services = new ServiceCollection().AddLogging(); services.AddFoundationInfrastructure(config); services.AddShippingInfrastructure(config); services.AddOrderInfrastructure(config);
        services.AddSingleton<IRequestContext, MyOnlineShop.Order.Tests.RequestContext>();
        Provider = services.BuildServiceProvider(); using var scope = Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ShippingDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Database.MigrateAsync();
    }
    public async Task<Guid> Order()
    {
        using var scope = Provider.CreateScope(); var services = scope.ServiceProvider;
        var method = await services.GetRequiredService<IShippingCommands>().CreateMethodAsync(Actor, Harness.Method(Guid.NewGuid().ToString("N"), 25m), default);
        var quote = await services.GetRequiredService<IShippingQuotes>().CalculateAsync(new() { ShippingMethodId = method.Id, Currency = "IRR", Address = Harness.Address() }, default);
        var variant = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var line = new PriceLineQuote(variant, 1, Guid.NewGuid(), "IRR", 100m, null, null, 0m, 0m, 100m, 100m, []);
        var item = OrderItem.Snapshot(variant, "FIXTURE", "Physical", "Physical", line, "IRR");
        var order = OrderAggregate.Create(Actor, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('A', 64), "IRR", now, now, [item], null, quote);
        order.Transition(OrderStatus.AwaitingPayment, now); order.Transition(OrderStatus.Paid, now); order.Transition(OrderStatus.Processing, now);
        var db = services.GetRequiredService<OrderDbContext>(); db.Orders.Add(order); await db.SaveChangesAsync(); return order.Id;
        // Trusted paid Order test fixture only; not a Payment producer/verification implementation.
    }
    public async Task<ShipmentDto> Create(Guid order)
    { using var scope = Provider.CreateScope(); return await scope.ServiceProvider.GetRequiredService<IShippingCommands>().CreateShipmentAsync(Actor, order, default); }
    public async Task<object> Change(ShipmentDto shipment)
    {
        using var scope = Provider.CreateScope();
        try { return await scope.ServiceProvider.GetRequiredService<IShippingCommands>().ChangeStatusAsync(Actor, shipment.Id, new() { ExpectedRevision = shipment.Revision, Status = "Preparing" }, default); }
        catch (ShippingException error) { return error; }
    }
    public async Task DisposeAsync()
    {
        if (Provider is not null) await Provider.DisposeAsync(); if (!_created) return;
        const string prefix = "MyOnlineShop_ShippingTests_";
        if (!_database.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(_database[prefix.Length..], "N", out _)) throw new InvalidOperationException("Unsafe disposable database cleanup.");
        SqlConnection.ClearAllPools(); var settings = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(settings.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync();
    }
}
public sealed class SqlTests(ShippingSqlFixture fixture) : IClassFixture<ShippingSqlFixture>
{
    [ShippingSqlFact]
    public async Task Order_and_shipment_retain_shipping_snapshot_cost_and_owner_in_sql()
    {
        var id = await fixture.Order(); var shipment = await fixture.Create(id); using var scope = fixture.Provider.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal(125m, order.PayableAmount); Assert.Equal(25m, order.ShippingCost); Assert.Equal(order.Shipping!.Address, shipment.Address);
        Assert.Equal(fixture.Actor, (await scope.ServiceProvider.GetRequiredService<ShippingDbContext>().Shipments.SingleAsync(value => value.OrderId == id)).UserId);
    }
    [ShippingSqlFact]
    public async Task Concurrent_creation_and_unique_order_constraint_prevent_duplicate_shipments()
    {
        var order = await fixture.Order();
        async Task<object> Create() { try { return await fixture.Create(order); } catch (ShippingException error) { return error; } }
        var results = await Task.WhenAll(Create(), Create()); Assert.Contains(results, value => value is ShipmentDto);
        Assert.All(results.OfType<ShippingException>(), error => Assert.Equal(409, error.StatusCode));
        using var scope = fixture.Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();
        var stored = Assert.Single(await db.Shipments.Where(value => value.OrderId == order).ToListAsync());
        db.Shipments.Add(Shipment.Create(order, fixture.Actor, new() { ShippingMethodId = stored.ShippingMethodId, MethodName = stored.MethodName,
            MethodCode = stored.MethodCode, Cost = stored.ShippingCost, Currency = stored.Currency, Address = stored.Address, RequiresTracking = stored.RequiresTracking }, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
    [ShippingSqlFact]
    public async Task Concurrent_same_revision_transition_has_one_winner_and_one_audit_record()
    {
        var shipment = await fixture.Create(await fixture.Order()); var results = await Task.WhenAll(fixture.Change(shipment), fixture.Change(shipment));
        Assert.Single(results.OfType<ShipmentDto>()); Assert.Equal(409, Assert.Single(results.OfType<ShippingException>()).StatusCode);
        using var scope = fixture.Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();
        Assert.Equal(1, await db.Audit.CountAsync(value => value.EntityId == shipment.Id && value.Action == "Status:Preparing"));
        Assert.Equal(ShipmentStatus.Preparing, (await db.Shipments.SingleAsync(value => value.Id == shipment.Id)).Status);
    }
    [ShippingSqlFact]
    public async Task Flushed_shipment_and_audit_roll_back_together_after_failure()
    {
        var shipment = await fixture.Create(await fixture.Order());
        using (var scope = fixture.Provider.CreateScope())
        {
            var services = scope.ServiceProvider; var db = services.GetRequiredService<ShippingDbContext>();
            await Assert.ThrowsAsync<IOException>(() => services.GetRequiredService<IShippingUnitOfWork>().ExecuteAsync<int>(async ct =>
            {
                var entity = await db.Shipments.SingleAsync(value => value.Id == shipment.Id, ct); entity.Transition(ShipmentStatus.Preparing, DateTimeOffset.UtcNow);
                db.Audit.Add(ShippingAudit.Record(entity.Id, fixture.Actor, "InjectedPreparing", DateTimeOffset.UtcNow, "rollback"));
                await db.SaveChangesAsync(ct); throw new IOException("Injected after SQL flush");
            }, default));
        }
        using var read = fixture.Provider.CreateScope(); var store = read.ServiceProvider.GetRequiredService<ShippingDbContext>();
        Assert.Equal(ShipmentStatus.Pending, (await store.Shipments.SingleAsync(value => value.Id == shipment.Id)).Status);
        Assert.False(await store.Audit.AnyAsync(value => value.EntityId == shipment.Id && value.Action == "InjectedPreparing"));
    }
    [ShippingSqlFact]
    public async Task Native_rowversion_detects_stale_update_and_history_guard_rejects_order_reassignment()
    {
        var shipment = await fixture.Create(await fixture.Order()); using var first = fixture.Provider.CreateScope(); using var second = fixture.Provider.CreateScope();
        var db1 = first.ServiceProvider.GetRequiredService<ShippingDbContext>(); var db2 = second.ServiceProvider.GetRequiredService<ShippingDbContext>();
        var a = await db1.Shipments.SingleAsync(value => value.Id == shipment.Id); var b = await db2.Shipments.SingleAsync(value => value.Id == shipment.Id);
        a.Transition(ShipmentStatus.Preparing, DateTimeOffset.UtcNow); b.Transition(ShipmentStatus.Preparing, DateTimeOffset.UtcNow);
        await db1.SaveChangesAsync(); await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        db2.ChangeTracker.Clear(); b = await db2.Shipments.SingleAsync(value => value.Id == shipment.Id);
        db2.Entry(b).Property(value => value.OrderId).CurrentValue = Guid.NewGuid(); await Assert.ThrowsAsync<InvalidOperationException>(() => db2.SaveChangesAsync());
    }
    [Fact(Skip = "Blocked: Phase 8 verified Payment -> paid Order integration is absent. Shipping tests use explicit paid Order domain fixtures and do not create Payment.")]
    public void Verified_payment_to_paid_order_to_shipment_end_to_end() => throw new NotSupportedException("Phase 8 producer pending.");
}
