using System.Text.Json;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Refund.Contracts;

namespace MyOnlineShop.Refund.Application;

public sealed class InventoryUnavailableConsumer(RefundRequests requests, IRefundPaymentPreparation? preparation = null) : IMessageConsumer
{
    public string ConsumerName => "refund.inventory-unavailable.v1";
    public string EventType => "inventory.unavailable";
    public int Version => 1;
    public IReadOnlyList<string> TransactionParticipants => preparation is null ? ["refund"] : ["refund", "payment", "ordering"];
    public async Task<IIntegrationEvent?> HandleAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        envelope.Validate();
        var value = envelope.Payload.Deserialize<InventoryUnavailableIntegrationEventV1>() ?? throw new InvalidMessageException();
        if (envelope.EventType != EventType || envelope.Version != Version || value.EventId != envelope.MessageId ||
            value.OccurredAtUtc != envelope.OccurredAtUtc || value.Reason != "StockUnavailable") throw new InvalidMessageException();
        if (preparation is not null) await preparation.PrepareAsync(value.PaymentId, value.OrderId, ct);
        await requests.RequestAsync(value.PaymentId, value.OrderId, envelope.CorrelationId, ct);
        return null;
    }
}
public sealed class RefundDueConsumer(RefundCompletion completion, IEnumerable<IRefundPaymentEvidence>? evidence = null) : IMessageConsumer
{
    public string ConsumerName => "refund.due.v1";
    public string EventType => "refunds.due";
    public int Version => 1;
    public IReadOnlyList<string> TransactionParticipants => new[] { "refund" }.Concat(
        (evidence ?? []).SelectMany(x => x.TransactionParticipants)).Distinct(StringComparer.Ordinal).ToArray();
    public async Task<IIntegrationEvent?> HandleAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        envelope.Validate();
        var value = envelope.Payload.Deserialize<RefundDueIntegrationEventV1>() ?? throw new InvalidMessageException();
        if (envelope.EventType != EventType || envelope.Version != Version || value.EventId != envelope.MessageId ||
            value.OccurredAtUtc != envelope.OccurredAtUtc || value.RefundId == Guid.Empty || value.Attempt < 1) throw new InvalidMessageException();
        await completion.ProcessAsync(value.RefundId, value.Attempt, ct); return null;
    }
}
