using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Inventory.Infrastructure;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Messaging.Tests;

public sealed class MessagingSqlFactAttribute : FactAttribute
{
    public MessagingSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MESSAGING_TEST_SQL_SERVER")))
            Skip = "SQL Server unavailable. MESSAGING_TEST_SQL_SERVER requires a future authorized disposable database test run.";
    }
}
internal sealed class Catalog : ICatalogVariantReferences
{
    public Task<CatalogVariantReference?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult<CatalogVariantReference?>(new(id, "TEST", "Physical", true));
}
internal sealed class ThrowInbox : SaveChangesInterceptor
{
    public Guid? MessageToFail { get; set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (data.Context is MessagingDbContext db && db.ChangeTracker.Entries<InboxMessage>().Any(entry => entry.Entity.MessageId == MessageToFail))
        { MessageToFail = null; throw new InvalidOperationException("Injected Inbox persistence failure."); }
        return ValueTask.FromResult(result);
    }
}
internal sealed class FailingConsumer : IMessageConsumer
{
    public string ConsumerName => "test.failure.v1";
    public string EventType => "payments.succeeded";
    public int Version => 1;
    public IReadOnlyList<string> TransactionParticipants => [];
    public Task<IIntegrationEvent?> HandleAsync(MessageEnvelope envelope, CancellationToken ct) => throw new IOException("credential=private-secret");
}
public sealed class MessagingSqlFixture : IAsyncLifetime
{
    private readonly string? _server = Environment.GetEnvironmentVariable("MESSAGING_TEST_SQL_SERVER");
    private readonly string _database = "MyOnlineShop_MessagingTests_" + Guid.NewGuid().ToString("N");
    private bool _created;
    public ServiceProvider Provider { get; private set; } = null!;
    internal Clock Clock { get; } = new();
    internal ThrowInbox Fault { get; } = new();
    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_server)) return;
        var options = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" };
        if (options.DataSource.Contains("localdb", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("LocalDB prohibited.");
        await using var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE [{_database}]"; await command.ExecuteNonQueryAsync(); _created = true;
        options.InitialCatalog = _database;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:SqlServer"] = options.ConnectionString,
            ["Messaging:RetrySeconds"] = "1", ["Messaging:MaxAttempts"] = "2" }).Build();
        var services = new ServiceCollection().AddLogging(); services.AddFoundationInfrastructure(config); services.AddInventoryInfrastructure(config);
        services.AddSingleton<TimeProvider>(Clock); services.AddSingleton<ICatalogVariantReferences, Catalog>();
        services.AddScoped<IRequestContext>(provider => provider.GetRequiredService<MessageRequestContext>());
        services.AddSingleton(Fault); services.AddDbContext<MessagingDbContext>((provider, builder) => builder.AddInterceptors(provider.GetRequiredService<ThrowInbox>()));
        Provider = services.BuildServiceProvider(); using var scope = Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MessagingDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database.MigrateAsync();
    }
    public async Task<(Guid Warehouse, Guid Variant)> Stock(long quantity)
    {
        using var scope = Provider.CreateScope(); var commands = scope.ServiceProvider.GetRequiredService<IInventoryCommands>();
        var warehouse = await commands.CreateWarehouseAsync(new() { Code = Guid.NewGuid().ToString("N"), Name = "Fixture" }, default);
        var variant = Guid.NewGuid();
        if (quantity > 0) await commands.ReceiveAsync(new() { OperationId = Guid.NewGuid(), WarehouseId = warehouse.Id, ProductVariantId = variant,
            Quantity = quantity, Reference = "fixture", Reason = "fixture" }, Guid.NewGuid(), default);
        return (warehouse.Id, variant);
    }
    public async Task<ConsumptionDisposition> Process(MessageEnvelope envelope)
    {
        using var scope = Provider.CreateScope(); var services = scope.ServiceProvider; services.GetRequiredService<MessageRequestContext>().CorrelationId = envelope.CorrelationId;
        return await services.GetRequiredService<IMessageProcessor>().ProcessAsync(services.GetServices<IMessageConsumer>().Single(), envelope, default);
    }
    public async Task Enqueue(MessageEnvelope envelope)
    {
        using var scope = Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var value = envelope.Payload.Deserialize<PaymentSucceededIntegrationEventV1>()!;
            await scope.ServiceProvider.GetRequiredService<IOutboxWriter>().EnqueueAsync(value, envelope.CorrelationId, default); await tx.CommitAsync();
        });
    }
    public async Task DisposeAsync()
    {
        if (Provider is not null) await Provider.DisposeAsync(); if (!_created) return;
        const string prefix = "MyOnlineShop_MessagingTests_";
        if (!_database.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(_database[prefix.Length..], "N", out _)) throw new InvalidOperationException("Unsafe fixture cleanup target.");
        SqlConnection.ClearAllPools(); var options = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync();
    }
}
public sealed class SqlTests(MessagingSqlFixture fixture) : IClassFixture<MessagingSqlFixture>
{
    [MessagingSqlFact]
    public async Task Business_and_outbox_commit_together_and_rollback_together_without_broker_dependency()
    {
        foreach (var rollback in new[] { false, true })
        {
            using var scope = fixture.Provider.CreateScope(); var services = scope.ServiceProvider; var db = services.GetRequiredService<MessagingDbContext>();
            var participant = services.GetServices<ILocalSqlTransactionParticipant>().Single(value => value.Name == "inventory");
            var id = Guid.NewGuid(); Guid warehouseId = default;
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await participant.EnlistAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), default);
                try
                {
                    var warehouse = await services.GetRequiredService<IInventoryCommands>().CreateWarehouseAsync(new() { Code = Guid.NewGuid().ToString("N"), Name = "Transaction" }, default); warehouseId = warehouse.Id;
                    var payment = Events.Payment(fixture.Clock, warehouseId, Guid.NewGuid()) with { EventId = id };
                    await services.GetRequiredService<IOutboxWriter>().EnqueueAsync(payment, "transaction", default);
                    if (rollback) await tx.RollbackAsync(); else await tx.CommitAsync();
                }
                finally { await participant.DetachAsync(default); }
            });
            db.ChangeTracker.Clear(); Assert.Equal(!rollback, await db.Outbox.AnyAsync(value => value.Id == id));
            Assert.Equal(!rollback, await services.GetRequiredService<InventoryDbContext>().Warehouses.AnyAsync(value => value.Id == warehouseId));
        }
    }
    [MessagingSqlFact]
    public async Task Outbox_claims_are_exclusive_crash_lease_recovers_same_event_and_success_marks_published()
    {
        var envelope = MessageEnvelope.From(Events.Payment(fixture.Clock, Guid.NewGuid(), Guid.NewGuid()), "lease"); await fixture.Enqueue(envelope);
        using var first = fixture.Provider.CreateScope(); using var second = fixture.Provider.CreateScope();
        var one = first.ServiceProvider.GetRequiredService<IOutboxDeliveryStore>(); var two = second.ServiceProvider.GetRequiredService<IOutboxDeliveryStore>();
        var batches = await Task.WhenAll(one.ClaimAsync(default), two.ClaimAsync(default));
        var claimed = Assert.Single(batches.SelectMany(value => value), value => value.Id == envelope.MessageId);
        var publisher = new Publisher(); await publisher.PublishAsync(claimed.Envelope(), default); // Crash: no CompleteAsync.
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3);
        var reclaimed = Assert.Single(await two.ClaimAsync(default), value => value.Id == envelope.MessageId); Assert.NotEqual(claimed.LeaseId, reclaimed.LeaseId);
        Assert.False(await one.RenewAsync(claimed, default)); await publisher.PublishAsync(reclaimed.Envelope(), default); await two.CompleteAsync(reclaimed, default);
        Assert.Equal(2, publisher.Published.Count); Assert.All(publisher.Published, value => Assert.Equal(envelope.MessageId, value.MessageId));
        var stored = await second.ServiceProvider.GetRequiredService<MessagingDbContext>().Outbox.AsNoTracking().SingleAsync(value => value.Id == envelope.MessageId);
        Assert.Equal("Published", stored.Status); Assert.NotNull(stored.ProcessedAtUtc);
    }
    [MessagingSqlFact]
    public async Task Broker_unavailable_keeps_persisted_message_and_retry_limit_marks_dead()
    {
        var envelope = MessageEnvelope.From(Events.Payment(fixture.Clock, Guid.NewGuid(), Guid.NewGuid()), "retry"); await fixture.Enqueue(envelope);
        using var scope = fixture.Provider.CreateScope(); var services = scope.ServiceProvider;
        var dispatcher = new OutboxDispatcher(services.GetRequiredService<IOutboxDeliveryStore>(), new Publisher { Unavailable = true },
            services.GetRequiredService<IOptions<MessagingOptions>>(), NullLogger<OutboxDispatcher>.Instance);
        await dispatcher.PublishBatchAsync(default); fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1); await dispatcher.PublishBatchAsync(default);
        var message = await services.GetRequiredService<MessagingDbContext>().Outbox.AsNoTracking().SingleAsync(value => value.Id == envelope.MessageId);
        Assert.Equal("Dead", message.Status); Assert.Equal(2, message.RetryCount); Assert.Null(message.ProcessedAtUtc); Assert.NotEmpty(message.Payload);
    }
    [MessagingSqlFact]
    public async Task Concurrent_duplicate_payment_message_deducts_stock_once_and_commits_one_inbox()
    {
        var stock = await fixture.Stock(1); var envelope = MessageEnvelope.From(Events.Payment(fixture.Clock, stock.Warehouse, stock.Variant), "duplicate");
        var outcomes = await Task.WhenAll(fixture.Process(envelope), fixture.Process(envelope));
        Assert.Contains(ConsumptionDisposition.Processed, outcomes); Assert.Contains(ConsumptionDisposition.Duplicate, outcomes);
        using var scope = fixture.Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(0, (await db.Stocks.SingleAsync(value => value.WarehouseId == stock.Warehouse && value.ProductVariantId == stock.Variant)).Quantity);
        var stockId = await db.Stocks.Where(value => value.WarehouseId == stock.Warehouse && value.ProductVariantId == stock.Variant).Select(value => value.Id).SingleAsync();
        Assert.Equal(1, await db.Movements.CountAsync(value => value.StockId == stockId && value.Type == MyOnlineShop.Inventory.Domain.MovementType.Sale));
        var messaging = scope.ServiceProvider.GetRequiredService<MessagingDbContext>(); var inbox = await messaging.Inbox.SingleAsync(value => value.MessageId == envelope.MessageId);
        Assert.Equal("Processed", inbox.Status);
        messaging.ChangeTracker.Clear(); messaging.Inbox.Add(new() { MessageId = inbox.MessageId, ConsumerName = inbox.ConsumerName, Fingerprint = inbox.Fingerprint, EnvelopeJson = inbox.EnvelopeJson });
        await Assert.ThrowsAsync<DbUpdateException>(() => messaging.SaveChangesAsync());
    }
    [MessagingSqlFact]
    public async Task Failed_second_line_rolls_back_first_deduction_and_records_rejected_inbox_with_failure_event()
    {
        var candidate = await fixture.Stock(0); var other = await fixture.Stock(0);
        var first = candidate.Warehouse.CompareTo(other.Warehouse) < 0 ? candidate : other;
        var missing = candidate.Warehouse.CompareTo(other.Warehouse) < 0 ? other : candidate;
        using (var seed = fixture.Provider.CreateScope())
            await seed.ServiceProvider.GetRequiredService<IInventoryCommands>().ReceiveAsync(new() { OperationId = Guid.NewGuid(), WarehouseId = first.Warehouse,
                ProductVariantId = first.Variant, Quantity = 1, Reference = "partial", Reason = "partial rollback fixture" }, Guid.NewGuid(), default);
        // Order lines by consumer's deterministic order so a successful line precedes the failure.
        var lines = new[] { new PaymentStockLine(first.Warehouse, first.Variant, 1), new PaymentStockLine(missing.Warehouse, missing.Variant, 1) };
        // The consumer processes the smaller warehouse first, proving rollback after a successful first line.
        var payment = Events.Payment(fixture.Clock, first.Warehouse, first.Variant) with { StockLines = lines };
        var result = await fixture.Process(MessageEnvelope.From(payment, "rejected")); Assert.Equal(ConsumptionDisposition.Rejected, result);
        using var scope = fixture.Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(1, (await db.Stocks.SingleAsync(value => value.WarehouseId == first.Warehouse && value.ProductVariantId == first.Variant)).Quantity);
        Assert.False(await db.Movements.AnyAsync(value => value.OperationId == MyOnlineShop.Inventory.Application.PaymentSucceededConsumer.OperationId(payment.PaymentId, lines[0])));
        var messaging = scope.ServiceProvider.GetRequiredService<MessagingDbContext>(); Assert.Equal("Rejected", (await messaging.Inbox.SingleAsync(value => value.MessageId == payment.EventId)).Status);
        Assert.Contains(await messaging.Outbox.ToListAsync(), value => value.EventType == "inventory.unavailable" && value.CorrelationId == "rejected");
        Assert.Equal(ConsumptionDisposition.Duplicate, await fixture.Process(MessageEnvelope.From(payment, "rejected")));
    }
    [MessagingSqlFact]
    public async Task Inbox_persistence_failure_rolls_back_inventory_and_successful_retry_is_safe()
    {
        var stock = await fixture.Stock(1); var envelope = MessageEnvelope.From(Events.Payment(fixture.Clock, stock.Warehouse, stock.Variant), "rollback-inbox");
        fixture.Fault.MessageToFail = envelope.MessageId; await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Process(envelope));
        using var scope = fixture.Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(1, (await db.Stocks.AsNoTracking().SingleAsync(value => value.WarehouseId == stock.Warehouse && value.ProductVariantId == stock.Variant)).Quantity);
        Assert.False(await scope.ServiceProvider.GetRequiredService<MessagingDbContext>().Inbox.AnyAsync(value => value.MessageId == envelope.MessageId));
        Assert.Equal(ConsumptionDisposition.Processed, await fixture.Process(envelope)); Assert.Equal(ConsumptionDisposition.Duplicate, await fixture.Process(envelope));
    }
    [MessagingSqlFact]
    public async Task Consumer_failure_attempts_are_persisted_bounded_and_safely_diagnosed()
    {
        var envelope = MessageEnvelope.From(Events.Payment(fixture.Clock, Guid.NewGuid(), Guid.NewGuid()), "consumer-retry");
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var scope = fixture.Provider.CreateScope(); var processor = scope.ServiceProvider.GetRequiredService<IMessageProcessor>();
            Assert.Equal(attempt == 1 ? ConsumptionDisposition.Retry : ConsumptionDisposition.Dead, await processor.ProcessAsync(new FailingConsumer(), envelope, default));
            fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
        }
        using var check = fixture.Provider.CreateScope(); var entry = await check.ServiceProvider.GetRequiredService<MessagingDbContext>().Inbox.SingleAsync(value => value.MessageId == envelope.MessageId);
        Assert.Equal("Dead", entry.Status); Assert.Equal(2, entry.RetryCount); Assert.NotNull(entry.LastAttemptAtUtc); Assert.Null(entry.ProcessedAtUtc);
        Assert.DoesNotContain("private-secret", entry.Error); Assert.NotEmpty(entry.EnvelopeJson);
    }
}
