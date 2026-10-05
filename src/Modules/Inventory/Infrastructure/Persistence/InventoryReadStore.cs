using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Inventory.Application;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Inventory.Domain;

namespace MyOnlineShop.Inventory.Infrastructure.Persistence;

internal sealed class InventoryReadStore(InventoryDbContext context) : IInventoryReadStore
{
    public async Task<WarehouseDto> GetWarehouseAsync(Guid id, CancellationToken ct) =>
        await context.Warehouses.AsNoTracking().Where(value => value.Id == id)
            .Select(value => new WarehouseDto(value.Id, value.Name, value.Code, value.IsActive)).SingleOrDefaultAsync(ct)
        ?? throw InventoryException.NotFound();
    public async Task<InventoryPage<WarehouseDto>> ListWarehousesAsync(InventoryQuery query, CancellationToken ct)
    {
        var warehouses = context.Warehouses.AsNoTracking();
        if (query.IsActive is not null) warehouses = warehouses.Where(value => value.IsActive == query.IsActive);
        var total = await warehouses.CountAsync(ct);
        var items = await warehouses.OrderBy(value => value.Name).ThenBy(value => value.Id).Skip(Offset(query)).Take(query.PageSize)
            .Select(value => new WarehouseDto(value.Id, value.Name, value.Code, value.IsActive)).ToListAsync(ct);
        return new(items, query.Page, query.PageSize, total);
    }
    private IQueryable<Stock> Stocks(InventoryQuery query)
    {
        var values = context.Stocks.AsNoTracking();
        if (query.WarehouseId is not null) values = values.Where(value => value.WarehouseId == query.WarehouseId);
        if (query.ProductVariantId is not null) values = values.Where(value => value.ProductVariantId == query.ProductVariantId);
        if (query.IsActive is not null) values = values.Where(value => value.IsActive == query.IsActive);
        if (query.LowStockOnly) values = values.Where(value => (value.IsActive &&
            context.Warehouses.Any(warehouse => warehouse.Id == value.WarehouseId && warehouse.IsActive) ? value.Quantity : 0) <= value.LowStockThreshold);
        return values;
    }
    private Expression<Func<Stock, StockDto>> StockProjection => stock => new(stock.Id, stock.WarehouseId,
        stock.ProductVariantId, stock.Quantity, stock.IsActive && context.Warehouses.Any(warehouse => warehouse.Id == stock.WarehouseId && warehouse.IsActive)
            ? stock.Quantity : 0, stock.LowStockThreshold, stock.IsActive);
    public async Task<StockDto> GetStockAsync(Guid warehouseId, Guid variantId, CancellationToken ct) =>
        await Stocks(new() { WarehouseId = warehouseId, ProductVariantId = variantId }).Select(StockProjection).SingleOrDefaultAsync(ct)
        ?? throw InventoryException.NotFound();
    public async Task<InventoryPage<StockDto>> ListStockAsync(InventoryQuery query, CancellationToken ct)
    {
        var values = Stocks(query); var total = await values.CountAsync(ct);
        var items = await StockPage(query).ToListAsync(ct);
        return new(items, query.Page, query.PageSize, total);
    }
    internal IQueryable<StockDto> StockPage(InventoryQuery query) => Stocks(query)
        .OrderBy(value => value.WarehouseId).ThenBy(value => value.ProductVariantId)
        .Skip(Offset(query)).Take(query.PageSize).Select(StockProjection);
    private IQueryable<InventoryMovement> Movements(InventoryQuery query)
    {
        var values = context.Movements.AsNoTracking();
        if (query.WarehouseId is not null) values = values.Where(value => context.Stocks.Any(stock => stock.Id == value.StockId && stock.WarehouseId == query.WarehouseId));
        if (query.ProductVariantId is not null) values = values.Where(value => context.Stocks.Any(stock => stock.Id == value.StockId && stock.ProductVariantId == query.ProductVariantId));
        if (query.MovementType is not null)
        {
            var type = Enum.Parse<MovementType>(query.MovementType);
            values = values.Where(value => value.Type == type);
        }
        return values;
    }
    public async Task<InventoryPage<MovementDto>> ListMovementsAsync(InventoryQuery query, CancellationToken ct)
    {
        var values = Movements(query); var total = await values.CountAsync(ct);
        var items = await MovementPage(query).ToListAsync(ct);
        return new(items, query.Page, query.PageSize, total);
    }
    internal IQueryable<MovementDto> MovementPage(InventoryQuery query)
    {
        var page = Movements(query).OrderByDescending(value => value.CreatedAtUtc).ThenBy(value => value.Id).Skip(Offset(query)).Take(query.PageSize);
        return from movement in page
                           join stock in context.Stocks.AsNoTracking() on movement.StockId equals stock.Id
                           orderby movement.CreatedAtUtc descending, movement.Id
                           select new MovementDto(movement.Id, movement.OperationId, stock.Id, stock.WarehouseId, stock.ProductVariantId,
                               movement.QuantityDelta, movement.QuantityBefore, movement.QuantityAfter, movement.Type.ToString(),
                               movement.Reference, movement.Reason, movement.ActorId, movement.CreatedAtUtc, movement.CorrelationId);
    }
    public async Task<InventoryPage<ReceiptDto>> ListReceiptsAsync(InventoryQuery query, CancellationToken ct)
    {
        var movements = Movements(query).Where(value => context.Receipts.Any(receipt => receipt.MovementId == value.Id));
        var total = await movements.CountAsync(ct);
        var items = await ReceiptPage(query).ToListAsync(ct);
        return new(items, query.Page, query.PageSize, total);
    }
    internal IQueryable<ReceiptDto> ReceiptPage(InventoryQuery query)
    {
        var movements = Movements(query).Where(value => context.Receipts.Any(receipt => receipt.MovementId == value.Id));
        var page = movements.OrderByDescending(value => value.CreatedAtUtc).ThenBy(value => value.Id).Skip(Offset(query)).Take(query.PageSize);
        return from movement in page
                           join stock in context.Stocks.AsNoTracking() on movement.StockId equals stock.Id
                           join receipt in context.Receipts.AsNoTracking() on movement.Id equals receipt.MovementId
                           orderby movement.CreatedAtUtc descending, movement.Id
                           select new ReceiptDto(receipt.Id, new(movement.Id, movement.OperationId, stock.Id, stock.WarehouseId, stock.ProductVariantId,
                               movement.QuantityDelta, movement.QuantityBefore, movement.QuantityAfter, movement.Type.ToString(),
                               movement.Reference, movement.Reason, movement.ActorId, movement.CreatedAtUtc, movement.CorrelationId));
    }
    private static int Offset(InventoryQuery query) => checked((query.Page - 1) * query.PageSize);
}
