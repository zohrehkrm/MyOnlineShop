using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Payment.Contracts;
using MyOnlineShop.Pricing.Contracts;
namespace MyOnlineShop.Payment.Domain;
public enum PaymentStatus { Created, Initiated, Succeeded, Failed }
public enum PaymentFulfillment { Pending, Fulfilled, RefundPending, Refunded }
public enum PaymentReconciliationReason { ProviderResultConflict, ProviderReferenceConflict, ProviderReceiptConflict, PersistenceConstraintConflict }
public sealed class PaymentRuleException(string message) : Exception(message);
public sealed class PaymentRecord {
 private PaymentRecord() { }
 public Guid Id { get; private set; }
 public Guid OrderId { get; private set; }
 public Guid UserId { get; private set; }
 public Guid IdempotencyKey { get; private set; }
 public decimal Amount { get; private set; }
 public string Currency { get; private set; } = "";
 public string Provider { get; private set; } = "";
 public string? ProviderReference { get; private set; }
 public string? ReceiptId { get; private set; }
 public string? CheckoutUrl { get; private set; }
 public bool ReconciliationRequired { get; private set; }
 public PaymentReconciliationReason? ReconciliationReason { get; private set; }
 public PaymentStatus Status { get; private set; }
 public PaymentFulfillment Fulfillment { get; private set; }
 public DateTimeOffset CreatedAtUtc { get; private set; }
 public DateTimeOffset? VerifiedAtUtc { get; private set; }
 public string CorrelationId { get; private set; } = "";
 public byte[] RowVersion { get; private set; } = [];
 private readonly List<PaymentAllocation> _allocations = [];
 public IReadOnlyList<PaymentAllocation> Allocations => _allocations.AsReadOnly();
 public static PaymentRecord Create(Guid order, Guid user, Guid key, decimal amount, string currency, string provider,
     IReadOnlyList<PaymentStockLine> lines, DateTimeOffset now, string correlation) {
  if (order == Guid.Empty || user == Guid.Empty || key == Guid.Empty || string.IsNullOrWhiteSpace(provider) || provider.Length > 64 ||
      lines.Count > 100 || lines.Any(x => x.WarehouseId == Guid.Empty || x.ProductVariantId == Guid.Empty || x.Quantity is < 1 or > 999) ||
      lines.Select(x => x.ProductVariantId).Distinct().Count() != lines.Count) throw new PaymentRuleException("Payment identity or allocation is invalid.");
  MoneyRules.Amount(amount, currency);
  var p = new PaymentRecord { Id = Guid.NewGuid(), OrderId = order, UserId = user, IdempotencyKey = key,
      Amount = amount, Currency = MoneyRules.Currency(currency), Provider = provider, CreatedAtUtc = now.ToUniversalTime(), CorrelationId = correlation };
  p._allocations.AddRange(lines.Select(x => new PaymentAllocation { PaymentId = p.Id, WarehouseId = x.WarehouseId, ProductVariantId = x.ProductVariantId, Quantity = x.Quantity }));
  return p;
 }
 public void Initiated(string reference, string url) {
  if (ReconciliationRequired) throw new PaymentRuleException("Payment requires reconciliation.");
  if (string.IsNullOrWhiteSpace(reference) || reference.Length > 128 || reference.Any(char.IsControl) ||
      !Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != "https" || !string.IsNullOrEmpty(address.UserInfo) || url.Length > 2048)
      throw new PaymentRuleException("Provider initiation result is invalid.");
  if (Status == PaymentStatus.Initiated && ProviderReference == reference && CheckoutUrl == url) return;
  if (Status != PaymentStatus.Created) throw new PaymentRuleException("Conflicting payment initiation.");
  ProviderReference = reference; CheckoutUrl = url; Status = PaymentStatus.Initiated;
 }
 public void Succeed(string receipt, DateTimeOffset now) {
  if (ReconciliationRequired) throw new PaymentRuleException("Payment requires reconciliation.");
  if (Status == PaymentStatus.Succeeded && ReceiptId == receipt) return;
  if (Status != PaymentStatus.Initiated || string.IsNullOrWhiteSpace(receipt) || receipt.Length > 128 || receipt.Any(char.IsControl))
      throw new PaymentRuleException("Payment success conflicts with its persisted state.");
  Status = PaymentStatus.Succeeded; ReceiptId = receipt; VerifiedAtUtc = now.ToUniversalTime(); CheckoutUrl = null;
 }
 public void RecoverVerifiedInitiation(string reference) {
  if (ReconciliationRequired) throw new PaymentRuleException("Payment requires reconciliation.");
  if(Status != PaymentStatus.Created || string.IsNullOrWhiteSpace(reference) || reference.Length > 128 || reference.Any(char.IsControl))
   throw new PaymentRuleException("Invalid initiation recovery.");
  ProviderReference=reference; Status=PaymentStatus.Initiated;
 }
 public void Fail() { if (Status == PaymentStatus.Failed) return; if (Status != PaymentStatus.Initiated) throw new PaymentRuleException("Invalid payment failure."); Status = PaymentStatus.Failed; CheckoutUrl = null; }
 public void Fulfill() {
  if (ReconciliationRequired || Status != PaymentStatus.Succeeded || Fulfillment is PaymentFulfillment.RefundPending or PaymentFulfillment.Refunded) throw new PaymentRuleException("Payment is not fulfillable.");
  Fulfillment = PaymentFulfillment.Fulfilled;
 }
 public void ClaimRefund() {
  if (ReconciliationRequired) {
   if (Fulfillment is PaymentFulfillment.RefundPending or PaymentFulfillment.Refunded) return;
   throw new PaymentRuleException("Payment requires reconciliation.");
  }
  if (Status != PaymentStatus.Succeeded || Fulfillment == PaymentFulfillment.Fulfilled) throw new PaymentRuleException("Payment is not refundable.");
  if (Fulfillment != PaymentFulfillment.Refunded) Fulfillment = PaymentFulfillment.RefundPending;
 }
 public void Refunded() { if (Fulfillment == PaymentFulfillment.Refunded) return; if (Fulfillment != PaymentFulfillment.RefundPending) throw new PaymentRuleException("Refund was not claimed."); Fulfillment = PaymentFulfillment.Refunded; }
 public PaymentSucceededIntegrationEventV1 SuccessEvent() {
  if (Status != PaymentStatus.Succeeded || VerifiedAtUtc is null) throw new PaymentRuleException("Payment is not verified.");
  return new(Id, VerifiedAtUtc.Value, Id, OrderId, UserId, Amount, Currency, _allocations.Select(x => new PaymentStockLine(x.WarehouseId, x.ProductVariantId, x.Quantity)).OrderBy(x => x.ProductVariantId).ToArray());
 }
 public void RequireReconciliation(PaymentReconciliationReason reason = PaymentReconciliationReason.ProviderResultConflict) {
  ReconciliationRequired = true; ReconciliationReason ??= reason; CheckoutUrl = null;
 }
 public PaymentDto Dto() => new(Id, OrderId, Status.ToString(), Fulfillment.ToString(), Amount, Currency, CheckoutUrl,ReconciliationRequired);
}
public sealed class PaymentAllocation { public Guid PaymentId { get; init; } public Guid WarehouseId { get; init; } public Guid ProductVariantId { get; init; } public long Quantity { get; init; } }

