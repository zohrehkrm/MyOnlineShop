using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Payment.Contracts;
using MyOnlineShop.Payment.Domain;
using MyOnlineShop.Wallet.Contracts;
namespace MyOnlineShop.Payment.Application;
public class PaymentException(string code, int status, string message, Exception? inner = null) : Exception(message, inner), IApplicationError {
 public string Code => code; public int StatusCode => status; public string SafeMessage => Message;
 public static PaymentException Invalid() => new("payment_validation",400,"Payment input or provider result is invalid.");
 public static PaymentException Conflict() => new("payment_conflict",409,"Payment conflicts with the current purchase state.");
 public static PaymentException Unavailable() => new("payment_provider_unavailable",503,"Payment provider or fulfillment configuration is unavailable.");
 public static PaymentException NotFound() => new("payment_not_found",404,"Payment was not found.");
}
public sealed class PaymentOptions { public Guid? FulfillmentWarehouseId { get; set; } public int ProviderTimeoutSeconds { get; set; } = 30; }
// Optional secure configuration for an explicitly registered real adapter. Never serialize/log this object.
public sealed class PaymentGatewayOptions {
 public string? Endpoint { get; set; }
 public string? MerchantId { get; set; }
 public string? Credential { get; set; }
 public bool IsValid() => Endpoint is null && MerchantId is null && Credential is null ||
  Uri.TryCreate(Endpoint,UriKind.Absolute,out var endpoint) && endpoint.Scheme == "https" && string.IsNullOrEmpty(endpoint.UserInfo) &&
  !string.IsNullOrWhiteSpace(MerchantId) && !string.IsNullOrWhiteSpace(Credential);
}
public sealed record ProviderInitiation(string Reference, string CheckoutUrl);
public enum ProviderPaymentStatus { Pending, Succeeded, Failed, Reversed, Chargeback }
public sealed record ProviderVerification(ProviderPaymentStatus Status, Guid MerchantPaymentId, string Reference, decimal Amount, string Currency, string? ReceiptId);
public interface IPaymentProvider {
 string Name { get; }
 bool IsConfigured => true;
 Task<ProviderInitiation> InitiateAsync(Guid merchantPaymentId, decimal amount, string currency, CancellationToken ct);
 // Implementations must query/authenticate the gateway server. Browser/callback status is never evidence.
 Task<ProviderVerification> VerifyAsync(Guid merchantPaymentId, string reference, CancellationToken ct);
}
public interface IPaymentStore {
 Task<PaymentRecord?> GetAsync(Guid id, CancellationToken ct);
 Task<PaymentRecord?> ByOrderAsync(Guid order, CancellationToken ct);
 Task<PaymentRecord?> ByKeyAsync(Guid user, Guid key, CancellationToken ct);
 void Add(PaymentRecord payment);
 Task SaveAsync(CancellationToken ct);
}
public interface IPaymentUnitOfWork {
 Task<T> ExecuteAsync<T>(Func<CancellationToken,Task<T>> action, CancellationToken ct);
 Task LockAsync(Guid orderId, CancellationToken ct);
}
public interface IPaymentAllocation { Task<IReadOnlyList<PaymentStockLine>> AllocateAsync(OrderPaymentSnapshot order, CancellationToken ct); }
public sealed class PaymentPersistenceConflictException(PaymentReconciliationReason reason, Exception original)
 : PaymentException("payment_persistence_conflict",409,"Payment persistence conflicts with provider evidence; reconciliation is required.",original) {
 public PaymentReconciliationReason Reason => reason;
}
public sealed class PaymentReconciliationRecordingException(Exception original, Exception recordingFailure)
 : PaymentException("payment_reconciliation_recording_failed",503,"Payment reconciliation could not be recorded; controlled recovery is required.",
     new AggregateException(original,recordingFailure));
public sealed class PaymentCommands(IPaymentStore store, IPaymentUnitOfWork unit, IPaymentProvider provider, IOrderPaymentWorkflow orders,
 IPaymentAllocation allocation, IOutboxWriter outbox, IOptions<PaymentOptions> options, TimeProvider clock, IRequestContext request,
 IWalletPaymentSafety walletSafety) : IPaymentCommands {
 public async Task<PaymentDto> InitiateAsync(Guid userId, InitiatePaymentInput input, CancellationToken ct) {
  if (userId == Guid.Empty || input.OrderId == Guid.Empty || input.IdempotencyKey == Guid.Empty) throw PaymentException.Invalid();
  if (!provider.IsConfigured) throw PaymentException.Unavailable();
  var p = await unit.ExecuteAsync(async token => {
   await unit.LockAsync(input.OrderId, token);
   var keyed = await store.ByKeyAsync(userId,input.IdempotencyKey,token);
   if (keyed is not null && keyed.OrderId != input.OrderId) throw PaymentException.Conflict();
   var existing = keyed ?? await store.ByOrderAsync(input.OrderId,token);
   if (existing is not null) { if (existing.UserId != userId) throw PaymentException.NotFound(); if(existing.IdempotencyKey != input.IdempotencyKey || existing.ReconciliationRequired) throw PaymentException.Conflict(); return existing; }
   var order = await orders.GetAsync(input.OrderId,token);
   if (order is null || order.UserId != userId) throw PaymentException.NotFound();
   if (order.Status != "AwaitingPayment") throw PaymentException.Conflict();
   if (order.Lines.Any(x => x.ProductKind == "Physical") && !order.HasShipping) throw PaymentException.Invalid();
   await CheckRefundCurrencyAsync(userId,order.Currency,token);
   var lines = await allocation.AllocateAsync(order,token);
   var created = PaymentRecord.Create(order.OrderId,userId,input.IdempotencyKey,order.Amount,order.Currency,provider.Name,lines,clock.GetUtcNow(),request.CorrelationId);
   store.Add(created); await orders.BindAsync(order.OrderId,created.Id,token); await store.SaveAsync(token); return created;
  },ct);
  if (p.Status != PaymentStatus.Created) return p.Dto();
  if (p.ReconciliationRequired) throw PaymentException.Conflict();
  if (p.Provider != provider.Name) throw PaymentException.Conflict();
  // Recheck recoverable Created records: a primary wallet may have appeared since the first attempt.
  await CheckRefundCurrencyAsync(p.UserId,p.Currency,ct);
  ProviderInitiation response;
  using var initiationTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
  initiationTimeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ProviderTimeoutSeconds));
  try { response = await provider.InitiateAsync(p.Id,p.Amount,p.Currency,initiationTimeout.Token).WaitAsync(TimeSpan.FromSeconds(options.Value.ProviderTimeoutSeconds),ct); }
  catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
  catch { throw PaymentException.Unavailable(); }
  try { var initiated = await unit.ExecuteAsync(async token => {
   await unit.LockAsync(p.OrderId,token); var current = await store.GetAsync(p.Id,token) ?? throw PaymentException.NotFound();
   if(current.ProviderReference is not null && current.ProviderReference != response.Reference) {
    current.RequireReconciliation(PaymentReconciliationReason.ProviderReferenceConflict); await store.SaveAsync(token);
   }
   if(current.Status is PaymentStatus.Succeeded or PaymentStatus.Failed || current.ReconciliationRequired) {
    return current.Dto();
   }
   current.Initiated(response.Reference,response.CheckoutUrl); await store.SaveAsync(token); return current.Dto();
  },ct);
  if(initiated.ReconciliationRequired) throw PaymentException.Conflict();
  return initiated; }
  catch (PaymentPersistenceConflictException error) { await RecordPersistenceConflictAsync(p,error); throw; }
 }
 public async Task<PaymentDto> VerifyAsync(Guid? userId, Guid paymentId, string reference, CancellationToken ct) {
  if (paymentId == Guid.Empty || string.IsNullOrWhiteSpace(reference) || reference.Length > 128 || reference.Any(char.IsControl)) throw PaymentException.Invalid();
  var p = await store.GetAsync(paymentId,ct) ?? throw PaymentException.NotFound();
  if (userId is not null && (userId == Guid.Empty || p.UserId != userId)) throw PaymentException.NotFound();
  if ((p.Status != PaymentStatus.Created && p.ProviderReference != reference) || p.Provider != provider.Name) throw PaymentException.Invalid();
  if (p.ReconciliationRequired) throw PaymentException.Conflict();
  if (p.Status is not (PaymentStatus.Created or PaymentStatus.Initiated or PaymentStatus.Failed or PaymentStatus.Succeeded)) throw PaymentException.Conflict();
  ProviderVerification proof;
  using var verificationTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
  verificationTimeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ProviderTimeoutSeconds));
  try { proof = await provider.VerifyAsync(p.Id,reference,verificationTimeout.Token).WaitAsync(TimeSpan.FromSeconds(options.Value.ProviderTimeoutSeconds),ct); }
  catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
  catch { throw PaymentException.Unavailable(); }
  if (!Enum.IsDefined(proof.Status) || proof.MerchantPaymentId != p.Id || proof.Reference != reference || proof.Amount != p.Amount || proof.Currency != p.Currency ||
      proof.Status == ProviderPaymentStatus.Succeeded && (string.IsNullOrWhiteSpace(proof.ReceiptId) || proof.ReceiptId.Length > 128 || proof.ReceiptId.Any(char.IsControl))) {
   // A malformed server result may represent a real charge. Retain a durable review marker, never guess success.
   await unit.ExecuteAsync(async token => {
    await unit.LockAsync(p.OrderId,token); var current=await store.GetAsync(p.Id,token) ?? throw PaymentException.NotFound();
    current.RequireReconciliation(); await store.SaveAsync(token);
    return true;
   },ct);
   throw PaymentException.Invalid();
  }
  PaymentDto result;
  try { result = await unit.ExecuteAsync(async token => {
   await unit.LockAsync(p.OrderId,token); var current = await store.GetAsync(p.Id,token) ?? throw PaymentException.NotFound();
   if(current.Status != PaymentStatus.Created && current.ProviderReference != proof.Reference) {
    current.RequireReconciliation(); await store.SaveAsync(token); return current.Dto();
   }
   if (current.ReconciliationRequired) return current.Dto();
   if (current.Status == PaymentStatus.Succeeded) {
    if(proof.Status != ProviderPaymentStatus.Succeeded || proof.ReceiptId != current.ReceiptId) {
     current.RequireReconciliation(); await store.SaveAsync(token);
    }
    return current.Dto();
   }
   if (current.Status == PaymentStatus.Failed) {
    if (proof.Status != ProviderPaymentStatus.Failed) { current.RequireReconciliation(); await store.SaveAsync(token); }
    return current.Dto();
   }
   if(current.Status == PaymentStatus.Created) {
    if(proof.Status == ProviderPaymentStatus.Pending) return current.Dto();
    current.RecoverVerifiedInitiation(proof.Reference);
   }
   if(proof.Status is ProviderPaymentStatus.Reversed or ProviderPaymentStatus.Chargeback) {
    current.RequireReconciliation(); await store.SaveAsync(token); return current.Dto();
   }
   if (proof.Status == ProviderPaymentStatus.Succeeded) {
    current.Succeed(proof.ReceiptId ?? "",clock.GetUtcNow()); await store.SaveAsync(token);
    await outbox.EnqueueAsync(current.SuccessEvent(),current.CorrelationId,token);
   } else if (proof.Status == ProviderPaymentStatus.Failed) {
    current.Fail(); await orders.FailAsync(current.OrderId,current.Id,token); await store.SaveAsync(token);
   }
   return current.Dto();
  },ct); }
  catch (PaymentPersistenceConflictException error) { await RecordPersistenceConflictAsync(p,error); throw; }
  if(result.ReconciliationRequired) throw PaymentException.Conflict();
  return result;
 }
 private async Task CheckRefundCurrencyAsync(Guid userId,string currency,CancellationToken ct) {
  var primary = await walletSafety.GetPrimaryCurrencyAsync(userId,ct);
  if(primary is not null && primary != currency)
   throw new PaymentException("payment_refund_currency",409,"The primary wallet cannot accept a refund in the order currency.");
 }
 private async Task RecordPersistenceConflictAsync(PaymentRecord payment,PaymentPersistenceConflictException original) {
  // The failed unit has rolled back/detached before this independent durable write. No provider payload is retained.
  using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
  try {
   await unit.ExecuteAsync(async token => {
    await unit.LockAsync(payment.OrderId,token);
    var current=await store.GetAsync(payment.Id,token) ?? throw PaymentException.NotFound();
    current.RequireReconciliation(original.Reason); await store.SaveAsync(token); return true;
   },recovery.Token);
  } catch(Exception recordingFailure) { throw new PaymentReconciliationRecordingException(original,recordingFailure); }
 }
}

