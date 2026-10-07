using System.ComponentModel.DataAnnotations;

namespace MyOnlineShop.Shipping.Contracts;

public sealed class ShippingAddressInput
{
    [Required, StringLength(200)] public string Recipient { get; init; } = "";
    [Required, RegularExpression(@"^\+[1-9][0-9]{7,14}$")] public string PhoneNumber { get; init; } = "";
    [Required, StringLength(100)] public string State { get; init; } = "";
    [Required, StringLength(100)] public string City { get; init; } = "";
    [Required, StringLength(500)] public string Street { get; init; } = "";
    [Required, StringLength(20)] public string PostalCode { get; init; } = "";
    [Required, RegularExpression("^[A-Za-z]{2}$")] public string CountryCode { get; init; } = "";
    [StringLength(100)] public string? Building { get; init; }
    [StringLength(30)] public string? Unit { get; init; }
}
public sealed record ShippingAddressDto(string Recipient, string PhoneNumber, string State, string City, string Street,
    string PostalCode, string CountryCode, string? Building, string? Unit);
public sealed class ShippingSelectionInput
{
    public Guid ShippingMethodId { get; init; }
    [Required] public ShippingAddressInput Address { get; init; } = new();
}
public sealed class ShippingQuoteInput
{
    public Guid ShippingMethodId { get; init; }
    [Required] public string Currency { get; init; } = "";
    [Required] public ShippingAddressInput Address { get; init; } = new();
}
public sealed class ShippingQuoteSnapshot
{
    public Guid ShippingMethodId { get; init; }
    public string MethodCode { get; init; } = "";
    public string MethodName { get; init; } = "";
    public decimal Cost { get; init; }
    public string Currency { get; init; } = "";
    public bool RequiresTracking { get; init; }
    public ShippingAddressDto Address { get; init; } = null!;
    public DateTimeOffset QuotedAtUtc { get; init; }
}
public interface IShippingQuotes
{
    Task<ShippingQuoteSnapshot> CalculateAsync(ShippingQuoteInput input, CancellationToken ct);
}
public sealed class ShippingMethodInput
{
    [Required, StringLength(100)] public string Name { get; init; } = "";
    [Required, StringLength(64)] public string Code { get; init; } = "";
    [StringLength(500)] public string? Description { get; init; }
    public decimal BaseCost { get; init; }
    [Required] public string Currency { get; init; } = "";
    public bool IsActive { get; init; } = true;
    public bool RequiresTracking { get; init; } = true;
}
public sealed class ShippingMethodUpdate
{
    public Guid ExpectedRevision { get; init; }
    [Required] public ShippingMethodInput Method { get; init; } = new();
}
public sealed class ShipmentStatusInput
{
    public Guid ExpectedRevision { get; init; }
    [Required] public string Status { get; init; } = "";
}
public sealed class ShipmentTrackingInput
{
    public Guid ExpectedRevision { get; init; }
    [Required, StringLength(100)] public string TrackingNumber { get; init; } = "";
    [Required, StringLength(100)] public string Carrier { get; init; } = "";
}
public sealed record ShippingMethodDto(Guid Id, string Name, string Code, string? Description, decimal BaseCost, string Currency,
    bool IsActive, bool RequiresTracking, Guid Revision);
public sealed record ShipmentDto(Guid Id, Guid OrderId, Guid ShippingMethodId, string MethodName, string MethodCode,
    ShippingAddressDto Address, decimal ShippingCost, string Currency, string Status, string? TrackingNumber, string? Carrier,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? ShippedAtUtc, DateTimeOffset? DeliveredAtUtc, Guid Revision);
public sealed class ShipmentListQuery
{
    [Range(1, int.MaxValue)] public int Page { get; init; } = 1;
    [Range(1, 100)] public int PageSize { get; init; } = 20;
    [RegularExpression("^(Pending|Preparing|Shipped|InTransit|Delivered|Cancelled)$")] public string? Status { get; init; }
    public Guid? OrderId { get; init; }
    public Guid? ShippingMethodId { get; init; }
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
}
public sealed record ShipmentSummaryDto(Guid Id, Guid OrderId, Guid ShippingMethodId, string MethodName, string Status,
    string? TrackingNumber, string? Carrier, DateTimeOffset CreatedAtUtc, Guid Revision);
public sealed record ShipmentPage(IReadOnlyList<ShipmentSummaryDto> Items, int Page, int PageSize, int TotalCount);
public interface IShippingCommands
{
    Task<ShippingMethodDto> CreateMethodAsync(Guid actor, ShippingMethodInput input, CancellationToken ct);
    Task<ShippingMethodDto> UpdateMethodAsync(Guid actor, Guid id, ShippingMethodUpdate input, CancellationToken ct);
    Task<ShipmentDto> CreateShipmentAsync(Guid actor, Guid orderId, CancellationToken ct);
    Task<ShipmentDto> ChangeStatusAsync(Guid actor, Guid id, ShipmentStatusInput input, CancellationToken ct);
    Task<ShipmentDto> AssignTrackingAsync(Guid actor, Guid id, ShipmentTrackingInput input, CancellationToken ct);
}
public interface IShippingQueries
{
    Task<IReadOnlyList<ShippingMethodDto>> AvailableMethodsAsync(string currency, CancellationToken ct);
    Task<IReadOnlyList<ShippingMethodDto>> MethodsAsync(CancellationToken ct);
    Task<ShipmentDto> GetMyOrderAsync(Guid userId, Guid orderId, CancellationToken ct);
    Task<ShipmentDto> GetAsync(Guid id, CancellationToken ct);
    Task<ShipmentPage> ListAsync(ShipmentListQuery query, CancellationToken ct);
}
