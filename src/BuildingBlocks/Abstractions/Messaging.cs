using System.Text.Json;

namespace MyOnlineShop.BuildingBlocks.Abstractions.Messaging;

public interface IIntegrationEvent
{
    Guid EventId { get; }
    string EventType { get; }
    int Version { get; }
    DateTimeOffset OccurredAtUtc { get; }
}
public sealed record MessageEnvelope(Guid MessageId, string EventType, int Version, DateTimeOffset OccurredAtUtc, string CorrelationId, JsonElement Payload)
{
    public string RoutingKey => $"{EventType}.v{Version}";
    public void Validate()
    {
        if (MessageId == Guid.Empty || string.IsNullOrWhiteSpace(EventType) || EventType.Length > 128 ||
            EventType.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_') || Version is < 1 or > 1000 ||
            OccurredAtUtc == default || OccurredAtUtc.Offset != TimeSpan.Zero || CorrelationId is null || CorrelationId.Length > 128 ||
            CorrelationId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.') || Payload.ValueKind != JsonValueKind.Object)
            throw new InvalidMessageException();
    }
    public static MessageEnvelope From(IIntegrationEvent value, string correlationId)
    {
        var envelope = new MessageEnvelope(value.EventId, value.EventType, value.Version, value.OccurredAtUtc,
            correlationId, JsonSerializer.SerializeToElement(value, value.GetType()));
        envelope.Validate(); return envelope;
    }
}
public sealed class InvalidMessageException() : Exception("Message envelope or contract is invalid.");
public interface IOutboxWriter { Task EnqueueAsync(IIntegrationEvent message, string correlationId, CancellationToken ct); }
public interface IMessagePublisher { Task PublishAsync(MessageEnvelope envelope, CancellationToken ct); }
public interface IMessageConsumer
{
    string ConsumerName { get; }
    string EventType { get; }
    int Version { get; }
    IReadOnlyList<string> TransactionParticipants { get; }
    // A rejection event requests rollback of all handler changes before durable rejection/outbox recording.
    Task<IIntegrationEvent?> HandleAsync(MessageEnvelope envelope, CancellationToken ct);
}
public enum ConsumptionDisposition { Processed, Duplicate, Rejected, Retry, Deferred, Dead }
public interface IMessageProcessor
{
    Task<ConsumptionDisposition> ProcessAsync(IMessageConsumer consumer, MessageEnvelope envelope, CancellationToken ct);
}
