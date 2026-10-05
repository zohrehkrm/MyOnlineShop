namespace MyOnlineShop.Inventory.Domain;

public sealed class InventoryRuleException(string message) : Exception(message);
public enum MovementType { Receipt = 1, Sale = 2, Return = 3, Damage = 4, ManualAdjustment = 5 }

public static class InventoryRules
{
    public const long MaximumQuantity = 1_000_000_000_000;
    public static string Required(string? value, int limit, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > limit)
            throw new InventoryRuleException($"{field} is required and must be within {limit} characters.");
        return value.Trim();
    }
    public static void Id(Guid id)
    { if (id == Guid.Empty) throw new InventoryRuleException("A valid identifier is required."); }
    public static void ValidateDelta(long delta, MovementType type)
    {
        if (delta == 0 || delta < -MaximumQuantity || delta > MaximumQuantity || !Enum.IsDefined(type) ||
            (type is MovementType.Receipt or MovementType.Return && delta < 0) ||
            (type is MovementType.Sale or MovementType.Damage && delta > 0))
            throw new InventoryRuleException("Quantity and movement type are inconsistent.");
    }
    public static long Apply(long current, long delta)
    {
        if (current < 0 || current > MaximumQuantity || delta == 0 || delta < -MaximumQuantity || delta > MaximumQuantity ||
            current + delta < 0 || current + delta > MaximumQuantity)
            throw new InventoryRuleException("Stock change would exceed available stock or supported limits.");
        return current + delta;
    }
}

public sealed class Warehouse
{
    private Warehouse() { }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string Code { get; private set; } = "";
    public bool IsActive { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public static Warehouse Create(string name, string code, bool active)
    { var value = new Warehouse { Id = Guid.NewGuid() }; value.Update(name, code, active); return value; }
    public void Update(string name, string code, bool active)
    {
        var normalized = InventoryRules.Required(code, 64, "Warehouse code").ToUpperInvariant();
        if (normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
            throw new InventoryRuleException("Warehouse code contains invalid characters.");
        Name = InventoryRules.Required(name, 100, "Warehouse name"); Code = normalized; IsActive = active;
    }
}

public sealed class Stock
{
    private Stock() { }
    public Guid Id { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid ProductVariantId { get; private set; }
    public long Quantity { get; private set; }
    public long LowStockThreshold { get; private set; }
    public bool IsActive { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public static Stock Create(Guid warehouseId, Guid variantId)
    {
        InventoryRules.Id(warehouseId); InventoryRules.Id(variantId);
        return new Stock { Id = Guid.NewGuid(), WarehouseId = warehouseId, ProductVariantId = variantId, IsActive = true };
    }
    public void Configure(bool active, long threshold)
    {
        if (threshold < 0 || threshold > InventoryRules.MaximumQuantity) throw new InventoryRuleException("Low stock threshold is invalid.");
        IsActive = active; LowStockThreshold = threshold;
    }
    // Quantity is changed only by the persistence layer's conditional UPDATE, never by tracked read/modify/write.
}

public sealed class InventoryMovement
{
    private InventoryMovement() { }
    public Guid Id { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid StockId { get; private set; }
    public long QuantityDelta { get; private set; }
    public long QuantityBefore { get; private set; }
    public long QuantityAfter { get; private set; }
    public MovementType Type { get; private set; }
    public string Reference { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public Guid ActorId { get; private set; }
    public string RequestHash { get; private set; } = "";
    public string CorrelationId { get; private set; } = "";
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public static InventoryMovement Create(Guid operationId, Guid stockId, long delta, long before, long after,
        MovementType type, string reference, string reason, Guid actorId, string hash, string correlation, DateTimeOffset now)
    {
        InventoryRules.Id(operationId); InventoryRules.Id(stockId); InventoryRules.Id(actorId);
        InventoryRules.ValidateDelta(delta, type);
        if (InventoryRules.Apply(before, delta) != after) throw new InventoryRuleException("Movement does not reconcile with stock.");
        return new InventoryMovement
        {
            Id = Guid.NewGuid(), OperationId = operationId, StockId = stockId, QuantityDelta = delta,
            QuantityBefore = before, QuantityAfter = after, Type = type,
            Reference = InventoryRules.Required(reference, 200, "Business reference"), Reason = InventoryRules.Required(reason, 500, "Reason"),
            ActorId = actorId, RequestHash = InventoryRules.Required(hash, 64, "Request hash"),
            CorrelationId = InventoryRules.Required(correlation, 128, "Correlation ID"), CreatedAtUtc = now.ToUniversalTime()
        };
    }
}

public sealed class StockReceipt
{
    private StockReceipt() { }
    public Guid Id { get; private set; }
    public Guid MovementId { get; private set; }
    public static StockReceipt From(InventoryMovement movement)
    {
        if (movement.Type != MovementType.Receipt) throw new InventoryRuleException("Receipt requires a receipt movement.");
        return new StockReceipt { Id = Guid.NewGuid(), MovementId = movement.Id };
    }
}
public sealed class StockAdjustment
{
    private StockAdjustment() { }
    public Guid Id { get; private set; }
    public Guid MovementId { get; private set; }
    public static StockAdjustment From(InventoryMovement movement)
    {
        if (movement.Type is not (MovementType.ManualAdjustment or MovementType.Damage))
            throw new InventoryRuleException("Adjustment requires a manual or damage movement.");
        return new StockAdjustment { Id = Guid.NewGuid(), MovementId = movement.Id };
    }
}
