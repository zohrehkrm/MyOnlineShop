using System.ComponentModel.DataAnnotations;

namespace MyOnlineShop.Inventory.Contracts;

public sealed class WarehouseInput
{
    [Required, StringLength(100)] public string Name { get; init; } = "";
    [Required, RegularExpression("^[A-Za-z0-9_.-]{1,64}$")] public string Code { get; init; } = "";
    public bool IsActive { get; init; } = true;
}
public sealed class StockSettingsInput
{
    public bool IsActive { get; init; } = true;
    [Range(0L, 1_000_000_000_000L)] public long LowStockThreshold { get; init; }
}
public class StockOperationInput
{
    public Guid OperationId { get; init; }
    public Guid WarehouseId { get; init; }
    public Guid ProductVariantId { get; init; }
    [Range(1L, 1_000_000_000_000L)] public long Quantity { get; init; }
    [Required, StringLength(200)] public string Reference { get; init; } = "";
    [Required, StringLength(500)] public string Reason { get; init; } = "";
}
public sealed class StockAdjustmentInput
{
    public Guid OperationId { get; init; }
    public Guid WarehouseId { get; init; }
    public Guid ProductVariantId { get; init; }
    [Range(-1_000_000_000_000L, 1_000_000_000_000L)] public long QuantityDelta { get; init; }
    [Required, RegularExpression("^(ManualAdjustment|Damage)$")] public string Type { get; init; } = "ManualAdjustment";
    [Required, StringLength(200)] public string Reference { get; init; } = "";
    [Required, StringLength(500)] public string Reason { get; init; } = "";
}
public sealed class InventoryQuery
{
    [Range(1, int.MaxValue)] public int Page { get; init; } = 1;
    [Range(1, 100)] public int PageSize { get; init; } = 20;
    public Guid? WarehouseId { get; init; }
    public Guid? ProductVariantId { get; init; }
    public bool? IsActive { get; init; }
    public bool LowStockOnly { get; init; }
    [RegularExpression("^(Receipt|Sale|Return|Damage|ManualAdjustment)$")] public string? MovementType { get; init; }
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
}
public sealed record WarehouseDto(Guid Id, string Name, string Code, bool IsActive);
public sealed record StockDto(Guid Id, Guid WarehouseId, Guid ProductVariantId, long Quantity, long AvailableQuantity, long LowStockThreshold, bool IsActive);
public sealed record MovementDto(Guid Id, Guid OperationId, Guid StockId, Guid WarehouseId, Guid ProductVariantId,
    long QuantityDelta, long QuantityBefore, long QuantityAfter, string Type, string Reference, string Reason,
    Guid ActorId, DateTimeOffset CreatedAtUtc, string CorrelationId);
public sealed record ReceiptDto(Guid Id, MovementDto Movement);
public sealed record AdjustmentDto(Guid Id, MovementDto Movement);
public sealed record InventoryPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public interface IInventoryCommands
{
    Task<WarehouseDto> CreateWarehouseAsync(WarehouseInput input, CancellationToken ct);
    Task<WarehouseDto> UpdateWarehouseAsync(Guid id, WarehouseInput input, CancellationToken ct);
    Task<StockDto> ConfigureStockAsync(Guid stockId, StockSettingsInput input, CancellationToken ct);
    Task<MovementDto> ReceiveAsync(StockOperationInput input, Guid actorId, CancellationToken ct);
    Task<MovementDto> AdjustAsync(StockAdjustmentInput input, Guid actorId, CancellationToken ct);
    Task<MovementDto> ReturnAsync(StockOperationInput input, Guid actorId, CancellationToken ct);
}
// Trusted internal entry point. A later payment/checkout handler must invoke it only after payment verification.
public interface IInventoryDeduction
{
    Task<MovementDto> DeductAsync(StockOperationInput input, Guid actorId, CancellationToken ct);
}
public interface IInventoryQueries
{
    Task<WarehouseDto> GetWarehouseAsync(Guid id, CancellationToken ct);
    Task<InventoryPage<WarehouseDto>> ListWarehousesAsync(InventoryQuery query, CancellationToken ct);
    Task<StockDto> GetStockAsync(Guid warehouseId, Guid variantId, CancellationToken ct);
    Task<InventoryPage<StockDto>> ListStockAsync(InventoryQuery query, CancellationToken ct);
    Task<InventoryPage<MovementDto>> ListMovementsAsync(InventoryQuery query, CancellationToken ct);
    Task<InventoryPage<ReceiptDto>> ListReceiptsAsync(InventoryQuery query, CancellationToken ct);
    Task<InventoryPage<AdjustmentDto>> ListAdjustmentsAsync(InventoryQuery query, CancellationToken ct);
}
