using MyOnlineShop.Shipping.Contracts;

namespace MyOnlineShop.Order.Contracts;

public sealed record OrderShippingSnapshot(Guid OrderId, Guid UserId, string Status, bool HasPhysicalItems, ShippingQuoteSnapshot? Shipping);
// Trusted read contract; Shipping never reads or writes Order tables/entities directly.
public interface IOrderShippingSnapshots { Task<OrderShippingSnapshot?> GetAsync(Guid orderId, CancellationToken ct); }
