using System.ComponentModel.DataAnnotations;
using MyOnlineShop.Shipping.Contracts;

namespace MyOnlineShop.Order.Contracts;

public sealed class CheckoutInput
{
    public Guid IdempotencyKey { get; init; }
    [Required] public string Currency { get; init; } = "";
    [StringLength(64)] public string? CouponCode { get; init; }
    public AddressInput? Address { get; init; }
    public ShippingSelectionInput? Shipping { get; init; }
}
public sealed class AddressInput
{
    [Required, StringLength(200)] public string Recipient { get; init; } = "";
    [Required, StringLength(500)] public string Street { get; init; } = "";
    [Required, StringLength(100)] public string City { get; init; } = "";
    [Required, StringLength(20)] public string PostalCode { get; init; } = "";
    [Required, RegularExpression("^[A-Za-z]{2}$")] public string CountryCode { get; init; } = "";
}
public sealed class ChangeOrderStatusInput { [Required] public string Status { get; init; } = ""; }
public sealed record AddressDto(string Recipient, string Street, string City, string PostalCode, string CountryCode);
public sealed record OrderItemDto(Guid Id, Guid ProductVariantId, string Sku, string ProductName, string ProductKind,
    Guid PriceId, Guid? DiscountId, decimal UnitPrice, decimal UnitDiscount, decimal DiscountAmount, decimal FinalUnitPrice, int Quantity, decimal LineTotal);
public sealed record OrderDto(Guid Id, string Status, string Currency, decimal Subtotal, decimal DiscountTotal, decimal PayableAmount,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset PricedAtUtc, AddressDto? Address, IReadOnlyList<OrderItemDto> Items,
    decimal ShippingCost = 0, ShippingQuoteSnapshot? Shipping = null);
public sealed record OrderSummaryDto(Guid Id, string Status, string Currency, decimal Subtotal, decimal DiscountTotal, decimal PayableAmount, DateTimeOffset CreatedAtUtc,
    decimal ShippingCost = 0);
public sealed record OrderPage(IReadOnlyList<OrderSummaryDto> Items, int Page, int PageSize, int TotalCount);
public interface ICheckoutCommands { Task<OrderDto> CreateAsync(Guid userId, CheckoutInput input, CancellationToken ct); }
public interface IOrderCommands
{
    Task<OrderDto> CancelAsync(Guid userId, Guid orderId, CancellationToken ct);
    Task<OrderDto> ChangeStatusAsync(Guid actorId, Guid orderId, string status, CancellationToken ct);
}
public interface IOrderQueries
{
    Task<OrderDto> GetMyAsync(Guid userId, Guid orderId, CancellationToken ct);
    Task<OrderPage> ListMyAsync(Guid userId, int page, int pageSize, CancellationToken ct);
    Task<OrderDto> GetAsync(Guid orderId, CancellationToken ct);
    Task<OrderPage> ListAsync(int page, int pageSize, CancellationToken ct);
}
