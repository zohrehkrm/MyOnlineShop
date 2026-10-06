using Microsoft.Extensions.Logging;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;
using MyOnlineShop.Inventory.Contracts;

namespace MyOnlineShop.Messaging.Tests;

internal sealed class Clock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class Publisher : IMessagePublisher
{
    public List<MessageEnvelope> Published { get; } = [];
    public bool Unavailable { get; set; }
    public Task PublishAsync(MessageEnvelope envelope, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); if (Unavailable) throw new IOException("password=must-not-log"); Published.Add(envelope); return Task.CompletedTask; }
}
// Explicit delivery-store substitute, not evidence of SQL lease/transaction correctness.
internal sealed class DeliveryStore(MessagingOptions options, Clock clock) : IOutboxDeliveryStore
{
    public List<OutboxMessage> Messages { get; } = [];
    public bool CrashOnComplete { get; set; }
    public bool LostLease { get; set; }
    public Task<IReadOnlyList<OutboxMessage>> ClaimAsync(CancellationToken ct)
    {
        var values = Messages.Where(value => value.Status == "Pending" && value.NextAttemptAtUtc <= clock.Now).ToArray();
        foreach (var value in values) { value.Status = "Publishing"; value.LeaseId = Guid.NewGuid(); value.RetryCount++; value.LastAttemptAtUtc = clock.Now; }
        return Task.FromResult<IReadOnlyList<OutboxMessage>>(values);
    }
    public Task<bool> RenewAsync(OutboxMessage message, CancellationToken ct) => Task.FromResult(!LostLease);
    public Task CompleteAsync(OutboxMessage message, CancellationToken ct)
    {
        if (CrashOnComplete) throw new IOException("private-connection-string");
        message.Status = "Published"; message.ProcessedAtUtc = clock.Now; message.LeaseId = null; return Task.CompletedTask;
    }
    public Task FailAsync(OutboxMessage message, bool retryable, string safeError, CancellationToken ct)
    {
        message.Status = DeliveryPolicy.FailureStatus(message.RetryCount, retryable, options);
        message.NextAttemptAtUtc = clock.Now.Add(options.Backoff(message.RetryCount)); message.Error = safeError; message.LeaseId = null; return Task.CompletedTask;
    }
    public void Add(MessageEnvelope envelope) => Messages.Add(new() { Id = envelope.MessageId, EventType = envelope.EventType, Version = envelope.Version,
        CreatedAtUtc = envelope.OccurredAtUtc, Payload = envelope.Payload.GetRawText(), CorrelationId = envelope.CorrelationId,
        Fingerprint = MessageFingerprint.Of(envelope), NextAttemptAtUtc = clock.Now });
}
internal sealed class Capture<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> format) => Messages.Add(format(state, error));
}
internal sealed class Settlement : IMessageSettlement
{
    public bool Ack { get; private set; }
    public bool? Requeue { get; private set; }
    public Task AcknowledgeAsync(CancellationToken ct) { Ack = true; return Task.CompletedTask; }
    public Task RejectAsync(bool requeue, CancellationToken ct) { Requeue = requeue; return Task.CompletedTask; }
}
internal sealed class DeferredProcessor : IMessageProcessor
{
    public TaskCompletionSource<ConsumptionDisposition> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<ConsumptionDisposition> ProcessAsync(IMessageConsumer consumer, MessageEnvelope envelope, CancellationToken ct) => Completion.Task;
}
internal static class Events
{
    public static PaymentSucceededIntegrationEventV1 Payment(Clock clock, Guid warehouse, Guid variant) =>
        new(Guid.NewGuid(), clock.Now, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m, "IRR", [new(warehouse, variant, 1)]);
}
