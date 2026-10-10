using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;
using MyOnlineShop.Inventory.Application;
using MyOnlineShop.Inventory.Contracts;
using Xunit;

namespace MyOnlineShop.Messaging.Tests;

public sealed class MessagingTests
{
    [Fact]
    public async Task Broker_outage_keeps_event_and_bounded_retries_preserve_diagnostic_state_without_secrets()
    {
        var clock = new Clock(); var settings = new MessagingOptions { MaxAttempts = 2 }; var store = new DeliveryStore(settings, clock);
        var envelope = MessageEnvelope.From(Events.Payment(clock, Guid.NewGuid(), Guid.NewGuid()), "correlation"); store.Add(envelope);
        var publisher = new Publisher { Unavailable = true }; var logger = new Capture<OutboxDispatcher>();
        var dispatcher = new OutboxDispatcher(store, publisher, Options.Create(settings), logger);
        await dispatcher.PublishBatchAsync(default); var entry = Assert.Single(store.Messages);
        Assert.Equal("Pending", entry.Status); Assert.Equal(1, entry.RetryCount); Assert.NotNull(entry.LastAttemptAtUtc); Assert.Null(entry.ProcessedAtUtc);
        await dispatcher.PublishBatchAsync(default); Assert.Equal(1, entry.RetryCount);
        clock.Now = entry.NextAttemptAtUtc; await dispatcher.PublishBatchAsync(default);
        Assert.Equal("Dead", entry.Status); Assert.Equal(2, entry.RetryCount); Assert.Empty(publisher.Published); Assert.NotEmpty(entry.Payload);
        Assert.DoesNotContain("must-not-log", entry.Error); Assert.All(logger.Messages, value => Assert.DoesNotContain("must-not-log", value));
    }
    [Fact]
    public async Task Publish_success_with_completion_failure_republishes_same_stable_event()
    {
        var clock = new Clock(); var settings = new MessagingOptions(); var store = new DeliveryStore(settings, clock) { CrashOnComplete = true };
        var envelope = MessageEnvelope.From(Events.Payment(clock, Guid.NewGuid(), Guid.NewGuid()), "test"); store.Add(envelope);
        var publisher = new Publisher(); var logger = new Capture<OutboxDispatcher>(); var dispatcher = new OutboxDispatcher(store, publisher, Options.Create(settings), logger);
        await dispatcher.PublishBatchAsync(default); var entry = Assert.Single(store.Messages);
        Assert.Equal("Pending", entry.Status); Assert.Single(publisher.Published);
        store.CrashOnComplete = false; clock.Now = entry.NextAttemptAtUtc; await dispatcher.PublishBatchAsync(default);
        Assert.Equal("Published", entry.Status); Assert.NotNull(entry.ProcessedAtUtc); Assert.Equal(2, publisher.Published.Count);
        Assert.All(publisher.Published, value => Assert.Equal(envelope.MessageId, value.MessageId));
        Assert.All(logger.Messages, value => Assert.DoesNotContain("private-connection-string", value));
    }
    [Fact]
    public async Task Invalid_payload_is_retained_as_dead_and_lost_lease_cannot_publish()
    {
        var clock = new Clock(); var settings = new MessagingOptions(); var store = new DeliveryStore(settings, clock); var publisher = new Publisher();
        store.Add(MessageEnvelope.From(Events.Payment(clock, Guid.NewGuid(), Guid.NewGuid()), "test")); store.Messages[0].Payload = "invalid-json";
        var dispatcher = new OutboxDispatcher(store, publisher, Options.Create(settings), new Capture<OutboxDispatcher>());
        await dispatcher.PublishBatchAsync(default); Assert.Equal("Dead", store.Messages[0].Status); Assert.Empty(publisher.Published);
        store.Add(MessageEnvelope.From(Events.Payment(clock, Guid.NewGuid(), Guid.NewGuid()), "test")); store.LostLease = true;
        await dispatcher.PublishBatchAsync(default); Assert.Empty(publisher.Published);
    }
    [Fact]
    public async Task Stock_one_duplicate_event_uses_existing_deduction_and_inbox_policy_once()
    {
        var stock = new MyOnlineShop.Inventory.Tests.CommandHarness(); await stock.Commands.ReceiveAsync(stock.Input(1), stock.Actor, default);
        var clock = new Clock(); var payment = Events.Payment(clock, stock.Warehouse, stock.Catalog.Id); var envelope = MessageEnvelope.From(payment, "stock-once");
        var consumer = new PaymentSucceededConsumer(stock.Commands, clock); InboxMessage? entry = null;
        var called = 0;
        foreach (var message in new[] { envelope, envelope })
        {
            if (DeliveryPolicy.InboxDisposition(entry, message, clock.Now) == ConsumptionDisposition.Duplicate) continue;
            called++; Assert.Null(await consumer.HandleAsync(message, default));
            entry = new() { MessageId = message.MessageId, ConsumerName = consumer.ConsumerName, Fingerprint = MessageFingerprint.Of(message), Status = "Processed", ProcessedAtUtc = clock.Now };
        }
        Assert.Equal(1, called); Assert.Equal(0, stock.Store.Stocks[(stock.Warehouse, stock.Catalog.Id)].Quantity);
        Assert.Single(stock.Store.Movements, value => value.Type == MyOnlineShop.Inventory.Domain.MovementType.Sale);
        // The existing operation idempotency also guards a distinct delivery ID for the same payment/business line.
        var another = MessageEnvelope.From(payment with { EventId = Guid.NewGuid() }, "stock-once"); Assert.Null(await consumer.HandleAsync(another, default));
        Assert.Single(stock.Store.Movements, value => value.Type == MyOnlineShop.Inventory.Domain.MovementType.Sale);
    }
    [Fact]
    public async Task Stock_failure_returns_clear_event_and_invalid_contract_never_calls_deduction()
    {
        var stock = new MyOnlineShop.Inventory.Tests.CommandHarness(); await stock.Commands.ReceiveAsync(stock.Input(1), stock.Actor, default);
        var clock = new Clock(); var payment = Events.Payment(clock, stock.Warehouse, stock.Catalog.Id) with { StockLines = [new(stock.Warehouse, stock.Catalog.Id, 2)] };
        var consumer = new PaymentSucceededConsumer(stock.Commands, clock); var envelope = MessageEnvelope.From(payment, "failure");
        var result = Assert.IsType<InventoryUnavailableIntegrationEventV1>(await consumer.HandleAsync(envelope, default));
        Assert.Equal(payment.PaymentId, result.PaymentId); Assert.Equal("StockUnavailable", result.Reason); Assert.NotEqual(payment.EventId, result.EventId);
        Assert.Equal(1, stock.Store.Stocks[(stock.Warehouse, stock.Catalog.Id)].Quantity);
        Assert.DoesNotContain(stock.Store.Movements, value => value.Type == MyOnlineShop.Inventory.Domain.MovementType.Sale);
        await Assert.ThrowsAsync<InvalidMessageException>(() => consumer.HandleAsync(MessageEnvelope.From(payment with { StockLines = [new(Guid.Empty, stock.Catalog.Id, 1)] }, "bad"), default));
        await Assert.ThrowsAsync<InvalidMessageException>(() => consumer.HandleAsync(envelope with { Version = 2 }, default));
    }
    [Fact]
    public async Task Acknowledge_occurs_only_after_durable_processing_returns_and_poison_is_rejected()
    {
        var stock = new MyOnlineShop.Inventory.Tests.CommandHarness(); var consumer = new PaymentSucceededConsumer(stock.Commands, new Clock());
        var envelope = MessageEnvelope.From(Events.Payment(new Clock(), stock.Warehouse, stock.Catalog.Id), "ack");
        var processor = new DeferredProcessor(); var settlement = new Settlement();
        var dispatch = new ConsumerDeliveryDispatcher(processor, new(), new Capture<ConsumerDeliveryDispatcher>()).DeliverAsync(consumer, envelope, settlement, default);
        Assert.False(settlement.Ack); processor.Completion.SetResult(ConsumptionDisposition.Processed); await dispatch; Assert.True(settlement.Ack);
        var dead = new DeferredProcessor(); var rejected = new Settlement(); dead.Completion.SetResult(ConsumptionDisposition.Dead);
        await new ConsumerDeliveryDispatcher(dead, new(), new Capture<ConsumerDeliveryDispatcher>()).DeliverAsync(consumer, envelope, rejected, default);
        Assert.False(rejected.Ack); Assert.False(rejected.Requeue);
    }
    [Fact]
    public void Inbox_payload_conflicts_and_persisted_retry_states_are_respected()
    {
        var clock = new Clock(); var envelope = MessageEnvelope.From(Events.Payment(clock, Guid.NewGuid(), Guid.NewGuid()), "test");
        var entry = new InboxMessage { Fingerprint = MessageFingerprint.Of(envelope), Status = "Pending", NextAttemptAtUtc = clock.Now.AddSeconds(30) };
        Assert.Equal(ConsumptionDisposition.Deferred, DeliveryPolicy.InboxDisposition(entry, envelope, clock.Now));
        Assert.Null(DeliveryPolicy.InboxDisposition(entry, envelope, clock.Now.AddMinutes(1)));
        entry.Status = "Processed"; Assert.Equal(ConsumptionDisposition.Duplicate, DeliveryPolicy.InboxDisposition(entry, envelope, clock.Now));
        Assert.Throws<InvalidMessageException>(() => DeliveryPolicy.InboxDisposition(entry, envelope with { CorrelationId = "changed" }, clock.Now));
        entry.Status = "Dead"; Assert.Equal(ConsumptionDisposition.Dead, DeliveryPolicy.InboxDisposition(entry, envelope, clock.Now));
    }
    [Fact]
    public void Options_and_envelopes_reject_invalid_settings_and_bound_backoff()
    {
        Assert.True(new MessagingOptions().IsValid()); Assert.False(new MessagingOptions { Enabled = true }.IsValid());
        Assert.False(new MessagingOptions { BatchSize = 0 }.IsValid()); Assert.False(new MessagingOptions { LeaseSeconds = 10 }.IsValid());
        Assert.Equal(TimeSpan.FromSeconds(60), new MessagingOptions().Backoff(100));
        var envelope = MessageEnvelope.From(Events.Payment(new Clock(), Guid.NewGuid(), Guid.NewGuid()), "test");
        Assert.Throws<InvalidMessageException>(() => (envelope with { MessageId = Guid.Empty }).Validate());
        Assert.Throws<InvalidMessageException>(() => (envelope with { EventType = "System.Arbitrary,Assembly" }).Validate());
        Assert.Throws<InvalidMessageException>(() => (envelope with { Version = 0 }).Validate());
    }
    [Fact(Skip = "Blocked: Phase 8 Payment producer/verification is not implemented. No fake Payment implementation is introduced.")]
    public void Verified_payment_to_outbox_to_broker_to_inventory_end_to_end_requires_Phase_8() => throw new NotSupportedException("Phase 8 producer pending.");
}
