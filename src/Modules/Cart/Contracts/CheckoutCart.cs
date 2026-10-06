namespace MyOnlineShop.Cart.Contracts;

public sealed record CheckoutCartSnapshot(CartDto Cart, Guid Revision);
public interface ICheckoutCart
{
    Task<CheckoutCartSnapshot> GetAsync(Guid userId, CancellationToken ct);
    Task ClearAsync(Guid userId, Guid cartId, Guid revision, CancellationToken ct);
}
