using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Inventory.Domain;
using MyOnlineShop.Reporting.Contracts;

namespace MyOnlineShop.Inventory.Infrastructure.Persistence;

public sealed class InventoryReporting(InventoryDbContext db) : IInventoryReportSource
{
    private IQueryable<Stock> Stock(Guid? warehouse, bool lowOnly)
    {
        var values = db.Stocks.AsNoTracking();
        if (warehouse is not null) values = values.Where(x => x.WarehouseId == warehouse);
        if (lowOnly) values = values.Where(x => x.IsActive && x.Quantity <= x.LowStockThreshold && db.Warehouses.Any(w => w.Id == x.WarehouseId && w.IsActive));
        return values;
    }
    public Task<long> LowStockVariantsAsync(Guid? warehouse, CancellationToken ct) => Stock(warehouse, true).Select(x => x.ProductVariantId).Distinct().LongCountAsync(ct);
    public async Task<ReportPage<StockReportRow>> StockAsync(Guid? warehouse, bool lowOnly, int page, int size, CancellationToken ct)
    {
        var values = Stock(warehouse, lowOnly);
        var rows = await (from stock in values join location in db.Warehouses.AsNoTracking() on stock.WarehouseId equals location.Id
            orderby stock.WarehouseId, stock.ProductVariantId
            select new StockReportRow(stock.WarehouseId, stock.ProductVariantId, stock.Quantity, stock.LowStockThreshold, stock.IsActive, location.IsActive))
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new(rows, page, size, await values.LongCountAsync(ct));
    }
    internal IQueryable<MovementSummaryDto> MovementQuery(ReportWindow window, Guid? warehouse) =>
        (from movement in db.Movements.AsNoTracking() join stock in db.Stocks.AsNoTracking() on movement.StockId equals stock.Id
         where movement.CreatedAtUtc >= window.FromUtc && movement.CreatedAtUtc < window.ToUtc && (warehouse == null || stock.WarehouseId == warehouse)
         select new { stock.WarehouseId, movement.Type, movement.QuantityDelta })
        .GroupBy(x => new { x.WarehouseId, x.Type })
        .OrderBy(g => g.Key.WarehouseId).ThenBy(g => g.Key.Type)
        .Select(g => new MovementSummaryDto(g.Key.WarehouseId, g.Key.Type.ToString(), g.LongCount(),
            g.Sum(x => x.QuantityDelta > 0 ? x.QuantityDelta : 0), g.Sum(x => x.QuantityDelta < 0 ? -x.QuantityDelta : 0), g.Sum(x => x.QuantityDelta)));
    public async Task<ReportPage<MovementSummaryDto>> MovementsAsync(ReportWindow window, Guid? warehouse, int page, int size, CancellationToken ct)
    {
        var query = MovementQuery(window, warehouse);
        return new(await query.Skip((page - 1) * size).Take(size).ToListAsync(ct), page, size, await query.LongCountAsync(ct));
    }
}
