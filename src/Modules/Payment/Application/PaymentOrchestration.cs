using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Payment.Domain;
using MyOnlineShop.Refund.Contracts;
using System.Text.Json;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
namespace MyOnlineShop.Payment.Application;
public sealed class PaymentOrchestration(IPaymentStore store, IPaymentUnitOfWork unit, IOrderPaymentWorkflow orders) : IInventoryPaymentOrchestration, IRefundPaymentPreparation, IRefundPaymentEvidence {
 public async Task ValidateAsync(PaymentSucceededIntegrationEventV1 value,CancellationToken ct) {
  await unit.LockAsync(value.OrderId,ct);
  var p = await store.GetAsync(value.PaymentId,ct) ?? throw new InvalidMessageException();
  if (p.ReconciliationRequired || p.Status != PaymentStatus.Succeeded || p.Fulfillment is PaymentFulfillment.RefundPending or PaymentFulfillment.Refunded ||
      value.OrderId != p.OrderId || value.ActorId != p.UserId || value.Amount != p.Amount || value.Currency != p.Currency ||
      value.EventId != p.Id || value.OccurredAtUtc != p.VerifiedAtUtc ||
      !p.SuccessEvent().StockLines.SequenceEqual(value.StockLines.OrderBy(x => x.ProductVariantId))) throw new InvalidMessageException();
 }
 public async Task ConfirmAsync(PaymentSucceededIntegrationEventV1 value,CancellationToken ct) {
  var p = await store.GetAsync(value.PaymentId,ct) ?? throw new InvalidMessageException();
  p.Fulfill(); await orders.ConfirmAsync(p.OrderId,p.Id,ct); await store.SaveAsync(ct);
 }
 public async Task PrepareAsync(Guid paymentId,Guid orderId,CancellationToken ct) {
  await unit.LockAsync(orderId,ct); var p = await store.GetAsync(paymentId,ct) ?? throw new InvalidMessageException();
  if(p.OrderId != orderId) throw new InvalidMessageException();
  p.ClaimRefund(); await orders.FailAsync(orderId,p.Id,ct); await store.SaveAsync(ct);
 }
 public async Task<VerifiedRefundPayment?> GetSucceededPaymentAsync(Guid paymentId,CancellationToken ct) {
  var p = await store.GetAsync(paymentId,ct);
  return p is { Status: PaymentStatus.Succeeded, Fulfillment: PaymentFulfillment.RefundPending or PaymentFulfillment.Refunded } ?
      new(p.Id,p.OrderId,p.UserId,p.Amount,p.Currency,p.ReconciliationRequired) : null;
 }
 public IReadOnlyList<string> TransactionParticipants => ["payment"];
 public async Task AcquireCreditLockAsync(Guid paymentId,CancellationToken ct) {
  var p = await store.GetAsync(paymentId,ct) ?? throw new InvalidMessageException();
  // Refund Inbox enlists Payment; the order lock excludes verification/hold changes through wallet posting.
  await unit.LockAsync(p.OrderId,ct);
 }
}
public sealed class PaymentRefundCompletedConsumer(IPaymentStore store,IPaymentUnitOfWork unit) : IMessageConsumer {
 public string ConsumerName => "payment.refund-completed.v1"; public string EventType => "refunds.completed"; public int Version => 1;
 public IReadOnlyList<string> TransactionParticipants => ["payment"];
 public async Task<IIntegrationEvent?> HandleAsync(MessageEnvelope envelope,CancellationToken ct) {
  envelope.Validate(); var value = envelope.Payload.Deserialize<RefundCompletedIntegrationEventV1>() ?? throw new InvalidMessageException();
  if(envelope.EventType != EventType || envelope.Version != 1 || value.EventId != envelope.MessageId || value.OccurredAtUtc != envelope.OccurredAtUtc || value.WalletTransactionId == Guid.Empty) throw new InvalidMessageException();
  await unit.LockAsync(value.OrderId,ct); var p = await store.GetAsync(value.PaymentId,ct) ?? throw new InvalidMessageException();
  if(p.OrderId != value.OrderId) throw new InvalidMessageException(); p.Refunded(); await store.SaveAsync(ct); return null;
 }
}

