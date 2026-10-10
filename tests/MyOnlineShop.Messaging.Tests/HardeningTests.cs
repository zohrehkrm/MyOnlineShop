using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;
using MyOnlineShop.Inventory.Application;
using Xunit;

namespace MyOnlineShop.Messaging.Tests;

public sealed class HardeningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_durable_envelope_cannot_inject_metadata_into_failure_logs(bool eventType)
    {
        var clock = new Clock(); var options = new MessagingOptions(); var store = new DeliveryStore(options, clock);
        store.Add(MessageEnvelope.From(Events.Payment(clock, Guid.NewGuid(), Guid.NewGuid()), "test"));
        if (eventType) store.Messages[0].EventType = "injected\nmetadata";
        else store.Messages[0].CorrelationId = "injected\nmetadata";
        var publisher = new Publisher(); var logger = new Capture<OutboxDispatcher>();
        await new OutboxDispatcher(store, publisher, Options.Create(options), logger).PublishBatchAsync(default);
        Assert.Equal("Dead", store.Messages[0].Status); Assert.Empty(publisher.Published);
        Assert.DoesNotContain("injected", Assert.Single(logger.Messages));
    }
    [Theory]
    [InlineData(" ")]
    [InlineData("line\nbreak")]
    [InlineData("secret=value")]
    [InlineData("unicode-\u202e")]
    public void Unsafe_correlation_is_rejected_at_the_envelope_boundary(string correlation)
    {
        var envelope = MessageEnvelope.From(Events.Payment(new Clock(), Guid.NewGuid(), Guid.NewGuid()), "safe-correlation");
        (envelope with { CorrelationId = "" }).Validate(); // Existing producers may have no correlation context.
        Assert.Throws<InvalidMessageException>(() => (envelope with { CorrelationId = correlation }).Validate());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrupted_or_oversized_durable_event_is_retained_dead_without_publishing(bool oversized)
    {
        var clock = new Clock(); var options = new MessagingOptions { MaximumPayloadBytes = 1024 }; var store = new DeliveryStore(options, clock);
        store.Add(MessageEnvelope.From(Events.Payment(clock, Guid.NewGuid(), Guid.NewGuid()), "test"));
        if (oversized)
        {
            var payload = JsonNode.Parse(store.Messages[0].Payload)!.AsObject(); payload["Padding"] = new string('A', 2048);
            store.Messages[0].Payload = payload.ToJsonString();
            store.Messages[0].Fingerprint = MessageFingerprint.Of(store.Messages[0].Envelope());
        }
        else
        {
            var payload = JsonNode.Parse(store.Messages[0].Payload)!.AsObject(); payload["Amount"] = 200;
            store.Messages[0].Payload = payload.ToJsonString();
        }
        var publisher = new Publisher(); var logger = new Capture<OutboxDispatcher>();
        await new OutboxDispatcher(store, publisher, Options.Create(options), logger).PublishBatchAsync(default);
        var row = Assert.Single(store.Messages);
        Assert.Equal("Dead", row.Status); Assert.NotEmpty(row.Payload); Assert.Null(row.ProcessedAtUtc);
        Assert.Empty(publisher.Published); Assert.Equal("Invalid event envelope.", row.Error);
    }

    [Fact]
    public async Task Cancellation_during_publish_never_completes_or_discards_the_claim()
    {
        var clock = new Clock(); var options = new MessagingOptions(); var store = new DeliveryStore(options, clock);
        store.Add(MessageEnvelope.From(Events.Payment(clock, Guid.NewGuid(), Guid.NewGuid()), "cancel"));
        using var cancel = new CancellationTokenSource();
        var dispatcher = new OutboxDispatcher(store, new CancellingPublisher(cancel), Options.Create(options), new Capture<OutboxDispatcher>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.PublishBatchAsync(cancel.Token));
        var row = Assert.Single(store.Messages);
        Assert.Equal("Publishing", row.Status); Assert.NotNull(row.LeaseId); Assert.Null(row.ProcessedAtUtc); Assert.NotEmpty(row.Payload);
        // SQL lease recovery is covered by the existing gated integration test, not this store double.
    }

    [Fact]
    public async Task Processing_failure_is_logged_safely_and_cancellation_never_acknowledges()
    {
        var clock = new Clock(); var envelope = MessageEnvelope.From(Events.Payment(clock, Guid.NewGuid(), Guid.NewGuid()), "retry");
        var stock = new MyOnlineShop.Inventory.Tests.CommandHarness(); var consumer = new PaymentSucceededConsumer(stock.Commands, clock);
        var logger = new Capture<ConsumerDeliveryDispatcher>(); var settlement = new Settlement();
        using var cancel = new CancellationTokenSource();
        var dispatch = new ConsumerDeliveryDispatcher(new FailingProcessor(cancel), new MessagingOptions { RetrySeconds = 1 }, logger)
            .DeliverAsync(consumer, envelope, settlement, cancel.Token);
        Assert.Single(logger.Messages); Assert.Contains("Infrastructure", logger.Messages[0]);
        Assert.DoesNotContain("private-secret", logger.Messages[0]); Assert.Contains(envelope.MessageId.ToString(), logger.Messages[0]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        Assert.False(settlement.Ack); Assert.Null(settlement.Requeue);
    }
    private sealed class CancellingPublisher(CancellationTokenSource cancel) : IMessagePublisher
    {
        public Task PublishAsync(MessageEnvelope envelope, CancellationToken ct) { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
    private sealed class FailingProcessor(CancellationTokenSource cancel) : IMessageProcessor
    {
        public Task<ConsumptionDisposition> ProcessAsync(IMessageConsumer consumer, MessageEnvelope envelope, CancellationToken ct)
        {
            cancel.Cancel();
            return Task.FromException<ConsumptionDisposition>(new IOException("private-secret"));
        }
    }
}
