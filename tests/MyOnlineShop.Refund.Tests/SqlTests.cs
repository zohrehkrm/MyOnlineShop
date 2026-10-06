using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Refund.Application;
using MyOnlineShop.Refund.Contracts;
using MyOnlineShop.Refund.Domain;
using MyOnlineShop.Refund.Infrastructure;
using MyOnlineShop.Refund.Infrastructure.Persistence;
using MyOnlineShop.Wallet.Infrastructure;
using MyOnlineShop.Wallet.Infrastructure.Persistence;
using Xunit;
using RefundAggregate = MyOnlineShop.Refund.Domain.Refund;

namespace MyOnlineShop.Refund.Tests;

public sealed class RefundSqlFactAttribute : FactAttribute
{
    public RefundSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REFUND_TEST_SQL_SERVER")))
            Skip = "SQL Server unavailable. REFUND_TEST_SQL_SERVER requires a future authorized disposable-database test run; LocalDB is prohibited.";
    }
}
internal sealed class SqlOrders : IOrderRefundSnapshots
{
    public ConcurrentDictionary<Guid, OrderRefundSnapshot> Values { get; } = new();
    public Task<OrderRefundSnapshot?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Values.GetValueOrDefault(id));
}
internal sealed class SqlEvidence : IRefundPaymentEvidence
{
    public ConcurrentDictionary<Guid, VerifiedRefundPayment> Values { get; } = new();
    public Task<VerifiedRefundPayment?> GetSucceededPaymentAsync(Guid id, CancellationToken ct) => Task.FromResult(Values.GetValueOrDefault(id));
}
internal sealed class SqlFault : SaveChangesInterceptor
{
    public Guid? FailCompletion { get; set; }
    public bool FailNextRequestOutbox { get; set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (data.Context is RefundDbContext refund && refund.ChangeTracker.Entries<RefundAggregate>()
            .Any(value => value.Entity.Id == FailCompletion && value.Entity.Status == RefundStatus.Completed))
        { FailCompletion = null; throw new IOException("Injected completion persistence failure"); }
        if (FailNextRequestOutbox && data.Context is MessagingDbContext messaging && messaging.ChangeTracker.Entries<OutboxMessage>()
            .Any(value => value.State == EntityState.Added && value.Entity.EventType == "refunds.requested"))
        { FailNextRequestOutbox = false; throw new IOException("Injected request Outbox failure"); }
        return ValueTask.FromResult(result);
    }
}
public sealed class RefundSqlFixture : IAsyncLifetime
{
    private readonly string? _server = Environment.GetEnvironmentVariable("REFUND_TEST_SQL_SERVER");
    private readonly string _database = "MyOnlineShop_RefundTests_" + Guid.NewGuid().ToString("N");
    private bool _created;
    public ServiceProvider Provider { get; private set; } = null!;
    internal Clock Clock { get; } = new();
    internal SqlOrders Orders { get; } = new();
    internal SqlEvidence Evidence { get; } = new();
    internal SqlFault Fault { get; } = new();
    internal MyOnlineShop.Wallet.Tests.IdentityReferences Identity { get; } = new();
    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_server)) return;
        var settings = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master", MultipleActiveResultSets = false };
        if (settings.DataSource.Contains("localdb", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("LocalDB prohibited.");
        await using var connection = new SqlConnection(settings.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync(); _created = true; settings.InitialCatalog = _database;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:SqlServer"] = settings.ConnectionString, ["Messaging:RetrySeconds"] = "1" }).Build();
        var services = new ServiceCollection().AddLogging(); services.AddFoundationInfrastructure(config);
        services.AddWalletInfrastructure(config); services.AddRefundInfrastructure(config);
        services.AddSingleton<TimeProvider>(Clock); services.AddSingleton<IOrderRefundSnapshots>(Orders);
        services.AddSingleton<IRefundPaymentEvidence>(Evidence); services.AddSingleton<IIdentityQueries>(Identity);
        services.AddScoped<IRequestContext>(provider => provider.GetRequiredService<MessageRequestContext>());
        services.AddDbContext<RefundDbContext>((_, options) => options.AddInterceptors(Fault));
        services.AddDbContext<MessagingDbContext>((_, options) => options.AddInterceptors(Fault));
        Provider = services.BuildServiceProvider(); using var scope = Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RefundDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<MessagingDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<WalletDbContext>().Database.MigrateAsync();
    }
    public (Guid Payment, Guid Order, Guid Refund) Scenario()
    {
        var payment = Guid.NewGuid(); var order = Guid.NewGuid();
        Orders.Values[order] = new(order, Identity.User, 100m, "IRR");
        Evidence.Values[payment] = new(payment, order, Identity.User, 100m, "IRR");
        return (payment, order, RefundAggregate.StableId("inventory-refund", payment));
    }
    public async Task Request(Guid payment, Guid order)
    {
        using var scope = Provider.CreateScope(); await scope.ServiceProvider.GetRequiredService<RefundRequests>().RequestAsync(payment, order, "sql-refund", default);
    }
    public async Task<ConsumptionDisposition> Deliver(IIntegrationEvent message, string consumer)
    {
        using var scope = Provider.CreateScope(); var services = scope.ServiceProvider;
        services.GetRequiredService<MessageRequestContext>().CorrelationId = "sql-refund";
        return await services.GetRequiredService<IMessageProcessor>().ProcessAsync(services.GetServices<IMessageConsumer>()
            .Single(value => value.ConsumerName == consumer), MessageEnvelope.From(message, "sql-refund"), default);
    }
    public async Task<RefundDueIntegrationEventV1> Schedule(Guid refund)
    {
        Clock.Now = Clock.Now.AddHours(2);
        using var scope = Provider.CreateScope(); var services = scope.ServiceProvider;
        await services.GetRequiredService<RefundScheduler>().ScheduleAsync(default);
        var state = await services.GetRequiredService<RefundDbContext>().Refunds.AsNoTracking().SingleAsync(value => value.Id == refund);
        var id = RefundAggregate.StableId($"refund-due-{state.AttemptCount}", state.PaymentId);
        var row = await services.GetRequiredService<MessagingDbContext>().Outbox.AsNoTracking().SingleAsync(value => value.Id == id);
        return JsonSerializer.Deserialize<RefundDueIntegrationEventV1>(row.Payload)!;
    }
    public async Task DisposeAsync()
    {
        if (Provider is not null) await Provider.DisposeAsync(); if (!_created) return;
        const string prefix = "MyOnlineShop_RefundTests_";
        if (!_database.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(_database[prefix.Length..], "N", out _))
            throw new InvalidOperationException("Unsafe disposable test database cleanup.");
        SqlConnection.ClearAllPools(); var settings = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(settings.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync();
    }
}
public sealed class RefundSqlTests(RefundSqlFixture fixture) : IClassFixture<RefundSqlFixture>
{
    [RefundSqlFact]
    public async Task Request_and_outbox_roll_back_together_then_can_be_retried()
    {
        var item = fixture.Scenario(); fixture.Fault.FailNextRequestOutbox = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Request(item.Payment, item.Order));
        using (var scope = fixture.Provider.CreateScope())
        {
            Assert.False(await scope.ServiceProvider.GetRequiredService<RefundDbContext>().Refunds.AnyAsync(value => value.Id == item.Refund));
            Assert.False(await scope.ServiceProvider.GetRequiredService<MessagingDbContext>().Outbox.AnyAsync(value => value.Id == RefundAggregate.StableId("refund-requested", item.Payment)));
        }
        await fixture.Request(item.Payment, item.Order);
    }
    [RefundSqlFact]
    public async Task Concurrent_requests_and_database_unique_keys_allow_one_refund()
    {
        var item = fixture.Scenario(); await Task.WhenAll(fixture.Request(item.Payment, item.Order), fixture.Request(item.Payment, item.Order));
        using var scope = fixture.Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<RefundDbContext>();
        Assert.Equal(1, await db.Refunds.CountAsync(value => value.PaymentId == item.Payment));
        db.Refunds.Add(RefundAggregate.Create(item.Order, item.Payment, fixture.Identity.User, 100m, "IRR", fixture.Clock.Now, TimeSpan.FromHours(1), "duplicate"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
    [RefundSqlFact]
    public async Task Inventory_failure_inbox_refund_and_request_outbox_commit_atomically()
    {
        var item = fixture.Scenario(); var value = new InventoryUnavailableIntegrationEventV1(Guid.NewGuid(), fixture.Clock.Now, item.Payment, item.Order, "StockUnavailable");
        Assert.Equal(ConsumptionDisposition.Processed, await fixture.Deliver(value, "refund.inventory-unavailable.v1"));
        Assert.Equal(ConsumptionDisposition.Duplicate, await fixture.Deliver(value, "refund.inventory-unavailable.v1"));
        using var scope = fixture.Provider.CreateScope(); Assert.Equal(RefundStatus.Pending, (await scope.ServiceProvider.GetRequiredService<RefundDbContext>().Refunds.SingleAsync(row => row.Id == item.Refund)).Status);
        Assert.False(await scope.ServiceProvider.GetRequiredService<WalletDbContext>().Ledger.AnyAsync(row => row.ReferenceId == item.Payment.ToString("N")));
    }
    [RefundSqlFact]
    public async Task Concurrent_due_schedulers_and_duplicate_delivery_credit_wallet_once()
    {
        var item = fixture.Scenario(); await fixture.Request(item.Payment, item.Order); fixture.Clock.Now = fixture.Clock.Now.AddHours(2);
        using var first = fixture.Provider.CreateScope(); using var second = fixture.Provider.CreateScope();
        var scheduled = await Task.WhenAll(first.ServiceProvider.GetRequiredService<RefundScheduler>().ScheduleAsync(default),
            second.ServiceProvider.GetRequiredService<RefundScheduler>().ScheduleAsync(default));
        Assert.Equal(1, scheduled.Sum());
        var due = await fixture.Schedule(item.Refund);
        using (var scope = fixture.Provider.CreateScope()) Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<RefundScheduler>().ScheduleAsync(default));
        var results = await Task.WhenAll(fixture.Deliver(due, "refund.due.v1"), fixture.Deliver(due, "refund.due.v1"));
        Assert.Contains(ConsumptionDisposition.Processed, results); Assert.Contains(ConsumptionDisposition.Duplicate, results);
        await fixture.Deliver(due with { EventId = Guid.NewGuid() }, "refund.due.v1");
        using var read = fixture.Provider.CreateScope(); var services = read.ServiceProvider;
        Assert.Equal(RefundStatus.Completed, (await services.GetRequiredService<RefundDbContext>().Refunds.SingleAsync(value => value.Id == item.Refund)).Status);
        Assert.Equal(1, await services.GetRequiredService<WalletDbContext>().Ledger.CountAsync(value => value.ReferenceType == "InventoryRefund" && value.ReferenceId == item.Payment.ToString("N")));
        Assert.True(await services.GetRequiredService<MessagingDbContext>().Outbox.AnyAsync(value => value.Id == RefundAggregate.StableId("refund-completed", item.Payment)));
    }
    [RefundSqlFact]
    public async Task Wallet_commit_survives_refund_completion_rollback_and_inbox_retry_deduplicates_credit()
    {
        var item = fixture.Scenario(); await fixture.Request(item.Payment, item.Order); var due = await fixture.Schedule(item.Refund);
        fixture.Fault.FailCompletion = item.Refund;
        Assert.Equal(ConsumptionDisposition.Retry, await fixture.Deliver(due, "refund.due.v1"));
        using (var scope = fixture.Provider.CreateScope())
        {
            Assert.Equal(RefundStatus.Processing, (await scope.ServiceProvider.GetRequiredService<RefundDbContext>().Refunds.SingleAsync(value => value.Id == item.Refund)).Status);
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<WalletDbContext>().Ledger.CountAsync(value => value.ReferenceId == item.Payment.ToString("N")));
        }
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
        Assert.Equal(ConsumptionDisposition.Processed, await fixture.Deliver(due, "refund.due.v1"));
        using var read = fixture.Provider.CreateScope(); Assert.Equal(1, await read.ServiceProvider.GetRequiredService<WalletDbContext>().Ledger.CountAsync(value => value.ReferenceId == item.Payment.ToString("N")));
    }
}
