using System.ComponentModel.DataAnnotations;

namespace MyOnlineShop.Cart.Contracts;

public sealed class AddCartItem
{
    public Guid ProductVariantId { get; init; }
    [Range(1, 999)] public int Quantity { get; init; }
}
public sealed class UpdateCartItemQuantity
{
    [Range(1, 999)] public int Quantity { get; init; }
}
public sealed record CartItemDto(Guid Id, Guid ProductVariantId, int Quantity, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record CartDto(Guid? Id, bool IsActive, DateTimeOffset? CreatedAtUtc, DateTimeOffset? UpdatedAtUtc,
    IReadOnlyList<CartItemDto> Items);
public interface ICartCommands
{
    Task<CartDto> AddAsync(Guid userId, AddCartItem command, CancellationToken ct);
    Task<CartDto> UpdateQuantityAsync(Guid userId, Guid itemId, UpdateCartItemQuantity command, CancellationToken ct);
    Task RemoveAsync(Guid userId, Guid itemId, CancellationToken ct);
    Task ClearAsync(Guid userId, CancellationToken ct);
}
public interface ICartQueries
{
    Task<CartDto> GetCurrentAsync(Guid userId, CancellationToken ct);
}
