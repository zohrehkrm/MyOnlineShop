namespace MyOnlineShop.Order.Contracts;
public sealed record OrderPaymentLine(Guid ProductVariantId,int Quantity,string ProductKind);
public sealed record OrderPaymentSnapshot(Guid OrderId,Guid UserId,string Status,decimal Amount,string Currency,IReadOnlyList<OrderPaymentLine> Lines,bool HasShipping = false);
public interface IOrderPaymentWorkflow {
 Task<OrderPaymentSnapshot?> GetAsync(Guid orderId,CancellationToken ct);
 Task BindAsync(Guid orderId,Guid paymentId,CancellationToken ct);
 Task ConfirmAsync(Guid orderId,Guid paymentId,CancellationToken ct);
 Task FailAsync(Guid orderId,Guid paymentId,CancellationToken ct);
}

