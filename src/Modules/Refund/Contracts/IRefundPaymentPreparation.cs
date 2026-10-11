namespace MyOnlineShop.Refund.Contracts;
// Claims exclusive full-refund eligibility and prevents subsequent fulfillment.
public interface IRefundPaymentPreparation { Task PrepareAsync(Guid paymentId,Guid orderId,CancellationToken ct); }

