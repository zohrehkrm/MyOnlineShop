using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Inventory.Contracts;

namespace MyOnlineShop.Inventory.Infrastructure.Persistence;

public sealed class InventoryAvailability(InventoryDbContext context) : IInventoryAvailability
{
    public async Task<IReadOnlyList<VariantAvailability>> GetAsync(IReadOnlyList<Guid> variantIds, CancellationToken ct)
    {
        if (variantIds.Count > 100 || variantIds.Any(id => id == Guid.Empty))
            throw new ArgumentException("A bounded list of valid variant IDs is required.", nameof(variantIds));
        return await context.Stocks.AsNoTracking().Where(stock => variantIds.Contains(stock.ProductVariantId) && stock.IsActive &&
                context.Warehouses.Any(warehouse => warehouse.Id == stock.WarehouseId && warehouse.IsActive))
            .GroupBy(stock => stock.ProductVariantId)
            .Select(group => new VariantAvailability(group.Key, group.Sum(stock => stock.Quantity))).ToListAsync(ct);
    }
}
