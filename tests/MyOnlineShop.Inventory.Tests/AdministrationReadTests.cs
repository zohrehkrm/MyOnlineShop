using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Inventory.Application;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Inventory.Domain;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Inventory.Tests;

public sealed class AdministrationReadTests
{
    [Fact]
    public async Task Adjustment_history_filters_warehouse_variant_and_half_open_dates_and_excludes_receipts()
    {
        using var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var warehouse = Warehouse.Create("Main", "MAIN", true); var stock = Stock.Create(warehouse.Id, Guid.NewGuid());
        var actor = Guid.NewGuid(); var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var receipt = InventoryMovement.Create(Guid.NewGuid(), stock.Id, 10, 0, 10, MovementType.Receipt, "receipt", "Goods", actor, "hash", "receipt-correlation", now.AddDays(-1));
        var adjustment = InventoryMovement.Create(Guid.NewGuid(), stock.Id, -2, 10, 8, MovementType.Damage, "damage", "Damaged goods", actor, "hash", "adjustment-correlation", now);
        db.Warehouses.Add(warehouse); db.Stocks.Add(stock); db.Movements.AddRange(receipt, adjustment);
        db.Receipts.Add(StockReceipt.From(receipt)); var row = StockAdjustment.From(adjustment); db.Adjustments.Add(row);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var queries = new InventoryQueries(new InventoryReadStore(db));
        var page = await queries.ListAdjustmentsAsync(new InventoryQuery { WarehouseId = warehouse.Id, ProductVariantId = stock.ProductVariantId, FromUtc = now, ToUtc = now.AddSeconds(1), PageSize = 1 }, default);
        var item = Assert.Single(page.Items); Assert.Equal(1, page.TotalCount); Assert.Equal(row.Id, item.Id);
        Assert.Equal(adjustment.Id, item.Movement.Id); Assert.Equal(actor, item.Movement.ActorId);
        Assert.Equal("adjustment-correlation", item.Movement.CorrelationId); Assert.Equal(8, item.Movement.QuantityAfter);
        Assert.Empty((await queries.ListAdjustmentsAsync(new InventoryQuery { ToUtc = now }, default)).Items);
        Assert.Empty((await queries.ListAdjustmentsAsync(new InventoryQuery { WarehouseId = Guid.NewGuid() }, default)).Items);
        Assert.Empty((await queries.ListAdjustmentsAsync(new InventoryQuery { Page = 2, PageSize = 1 }, default)).Items);
        Assert.Empty(db.ChangeTracker.Entries()); // Read projections do not track business entities.
    }
}
