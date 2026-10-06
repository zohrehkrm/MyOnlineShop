using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;

namespace MyOnlineShop.Refund.Contracts;

// Future Phase 8 implements this read-only evidence contract from its authoritative verified payment records.
// No default/fake evidence implementation is registered in production.
public interface IRefundPaymentEvidence
{
    Task<VerifiedRefundPayment?> GetSucceededPaymentAsync(Guid paymentId, CancellationToken ct);
}
public sealed record VerifiedRefundPayment(Guid PaymentId, Guid OrderId, Guid UserId, decimal Amount, string Currency);
public sealed record RefundRequestedIntegrationEventV1(Guid EventId, DateTimeOffset OccurredAtUtc, Guid RefundId, Guid OrderId, Guid PaymentId) : IIntegrationEvent
{ public string EventType => "refunds.requested"; public int Version => 1; }
public sealed record RefundDueIntegrationEventV1(Guid EventId, DateTimeOffset OccurredAtUtc, Guid RefundId, int Attempt) : IIntegrationEvent
{ public string EventType => "refunds.due"; public int Version => 1; }
public sealed record RefundCompletedIntegrationEventV1(Guid EventId, DateTimeOffset OccurredAtUtc, Guid RefundId, Guid OrderId,
    Guid PaymentId, Guid WalletTransactionId) : IIntegrationEvent
{ public string EventType => "refunds.completed"; public int Version => 1; }
public sealed record RefundFailedIntegrationEventV1(Guid EventId, DateTimeOffset OccurredAtUtc, Guid RefundId, string FailureCode) : IIntegrationEvent
{ public string EventType => "refunds.failed"; public int Version => 1; }
