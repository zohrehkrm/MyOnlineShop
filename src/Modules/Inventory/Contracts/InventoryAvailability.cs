namespace MyOnlineShop.Inventory.Contracts;

public sealed record VariantAvailability(Guid ProductVariantId, long AvailableQuantity);
// Availability is advisory; this contract performs no reservation, deduction or locks.
public interface IInventoryAvailability
{
    Task<IReadOnlyList<VariantAvailability>> GetAsync(IReadOnlyList<Guid> variantIds, CancellationToken ct);
}
