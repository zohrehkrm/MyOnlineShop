namespace MyOnlineShop.Order.Contracts;

// Trusted internal read contract; contains no address/profile data or mutation authority.
public sealed record OrderRefundSnapshot(Guid OrderId, Guid UserId, decimal PayableAmount, string Currency);
public interface IOrderRefundSnapshots
{
    Task<OrderRefundSnapshot?> GetAsync(Guid orderId, CancellationToken ct);
}
