using System.Text.Json;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Refund.Contracts;

namespace MyOnlineShop.Refund.Application;

public sealed class InventoryUnavailableConsumer(RefundRequests requests) : IMessageConsumer
{
    public string ConsumerName => "refund.inventory-unavailable.v1";
    public string EventType => "inventory.unavailable";
    public int Version => 1;
    public IReadOnlyList<string> TransactionParticipants => ["refund"];
    public async Task<IIntegrationEvent?> HandleAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        envelope.Validate();
        var value = envelope.Payload.Deserialize<InventoryUnavailableIntegrationEventV1>() ?? throw new InvalidMessageException();
        if (envelope.EventType != EventType || envelope.Version != Version || value.EventId != envelope.MessageId ||
            value.OccurredAtUtc != envelope.OccurredAtUtc || value.Reason != "StockUnavailable") throw new InvalidMessageException();
        await requests.RequestAsync(value.PaymentId, value.OrderId, envelope.CorrelationId, ct);
        return null;
    }
}
public sealed class RefundDueConsumer(RefundCompletion completion) : IMessageConsumer
{
    public string ConsumerName => "refund.due.v1";
    public string EventType => "refunds.due";
    public int Version => 1;
    public IReadOnlyList<string> TransactionParticipants => ["refund"];
    public async Task<IIntegrationEvent?> HandleAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        envelope.Validate();
        var value = envelope.Payload.Deserialize<RefundDueIntegrationEventV1>() ?? throw new InvalidMessageException();
        if (envelope.EventType != EventType || envelope.Version != Version || value.EventId != envelope.MessageId ||
            value.OccurredAtUtc != envelope.OccurredAtUtc || value.RefundId == Guid.Empty || value.Attempt < 1) throw new InvalidMessageException();
        await completion.ProcessAsync(value.RefundId, value.Attempt, ct); return null;
    }
}
