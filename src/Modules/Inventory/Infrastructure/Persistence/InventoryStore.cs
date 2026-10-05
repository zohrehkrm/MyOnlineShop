using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MyOnlineShop.Inventory.Application;
using MyOnlineShop.Inventory.Domain;

namespace MyOnlineShop.Inventory.Infrastructure.Persistence;

public sealed class InventoryStore(InventoryDbContext context) : IInventoryStore
{
    public const string ConditionalQuantitySql = """
        UPDATE s
        SET Quantity = s.Quantity + @delta
        OUTPUT INSERTED.Id, DELETED.Quantity, INSERTED.Quantity
        FROM [inventory].[Stocks] AS s
        INNER JOIN [inventory].[Warehouses] AS w ON w.Id = s.WarehouseId
        WHERE s.WarehouseId = @warehouse AND s.ProductVariantId = @variant
          AND s.IsActive = 1 AND w.IsActive = 1
          AND s.Quantity + @delta >= 0 AND s.Quantity + @delta <= 1000000000000;
        """;
    public Task<Warehouse?> GetWarehouseAsync(Guid id, CancellationToken ct) => context.Warehouses.SingleOrDefaultAsync(value => value.Id == id, ct);
    public Task<bool> WarehouseCodeExistsAsync(string code, Guid? excludingId, CancellationToken ct) =>
        context.Warehouses.AnyAsync(value => value.Code == code && value.Id != excludingId, ct);
    public void AddWarehouse(Warehouse warehouse) => context.Warehouses.Add(warehouse);
    public Task<Stock?> GetStockAsync(Guid id, CancellationToken ct) => context.Stocks.SingleOrDefaultAsync(value => value.Id == id, ct);
    public async Task EnsureStockAsync(Guid warehouseId, Guid variantId, CancellationToken ct)
    {
        if (await context.Stocks.AnyAsync(value => value.WarehouseId == warehouseId && value.ProductVariantId == variantId, ct)) return;
        context.Stocks.Add(Stock.Create(warehouseId, variantId));
        await context.SaveChangesAsync(ct);
    }
    public async Task<QuantityChange?> ChangeQuantityAsync(Guid warehouseId, Guid variantId, long delta, CancellationToken ct)
    {
        if (delta == 0 || delta < -InventoryRules.MaximumQuantity || delta > InventoryRules.MaximumQuantity)
            throw InventoryException.Invalid();
        var transaction = context.Database.CurrentTransaction ?? throw new InvalidOperationException("Inventory quantity changes require a transaction.");
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandTimeout = context.Database.GetCommandTimeout() ?? 30;
        command.CommandText = ConditionalQuantitySql;
        foreach (var (name, type, value) in new (string, DbType, object)[]
        { ("@delta", DbType.Int64, delta), ("@warehouse", DbType.Guid, warehouseId), ("@variant", DbType.Guid, variantId) })
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.DbType = type;
            parameter.Value = value; command.Parameters.Add(parameter);
        }
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null; // Zero affected rows: no movement may be created.
        var change = new QuantityChange(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2));
        if (await reader.ReadAsync(ct)) throw new InvalidOperationException("Duplicate warehouse/variant inventory records.");
        return change;
    }
    public async Task<StoredOperation?> FindOperationAsync(Guid operationId, CancellationToken ct)
    {
        return await (from movement in context.Movements.AsNoTracking()
                      join stock in context.Stocks.AsNoTracking() on movement.StockId equals stock.Id
                      where movement.OperationId == operationId
                      select new StoredOperation(movement.RequestHash, new(movement.Id, movement.OperationId, stock.Id,
                          stock.WarehouseId, stock.ProductVariantId, movement.QuantityDelta, movement.QuantityBefore, movement.QuantityAfter,
                          movement.Type.ToString(), movement.Reference, movement.Reason, movement.ActorId, movement.CreatedAtUtc, movement.CorrelationId)))
            .SingleOrDefaultAsync(ct);
    }
    public void AddMovement(InventoryMovement movement) => context.Movements.Add(movement);
    public void AddReceipt(StockReceipt receipt) => context.Receipts.Add(receipt);
    public void AddAdjustment(StockAdjustment adjustment) => context.Adjustments.Add(adjustment);
}
