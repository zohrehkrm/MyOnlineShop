using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Inventory.Application;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Inventory.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyOnlineShop.Inventory.Tests;

internal sealed class TestRequest : IRequestContext { public string CorrelationId => "inventory-test"; }
internal sealed class VariantReferences : ICatalogVariantReferences
{
    public Guid Id { get; } = Guid.NewGuid();
    public bool Active { get; set; } = true;
    public string Kind { get; set; } = "Physical";
    public Task<CatalogVariantReference?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(id == Id ? new CatalogVariantReference(Id, "TEST-SKU", Kind, Active) : null);
}
// Application behavior test double. It is not evidence of SQL atomicity, locking or concurrency behavior.
internal sealed class MemoryStore : IInventoryStore, IInventoryUnitOfWork
{
    public List<Warehouse> Warehouses { get; } = [];
    public Dictionary<(Guid Warehouse, Guid Variant), (Stock Stock, long Quantity)> Stocks { get; } = [];
    public List<InventoryMovement> Movements { get; } = [];
    public List<StockReceipt> Receipts { get; } = [];
    public List<StockAdjustment> Adjustments { get; } = [];
    public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) => ExecuteCoreAsync(action, ct);
    private async Task<T> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        try { return await action(ct); }
        catch (InventoryRuleException error) { throw InventoryException.Invalid(error.Message); }
    }
    public Task LockAsync(string resource, CancellationToken ct) => Task.CompletedTask;
    public Task<Warehouse?> GetWarehouseAsync(Guid id, CancellationToken ct) => Task.FromResult(Warehouses.SingleOrDefault(value => value.Id == id));
    public Task<bool> WarehouseCodeExistsAsync(string code, Guid? excludingId, CancellationToken ct) =>
        Task.FromResult(Warehouses.Any(value => value.Code == code && value.Id != excludingId));
    public void AddWarehouse(Warehouse value) => Warehouses.Add(value);
    public Task<Stock?> GetStockAsync(Guid id, CancellationToken ct) => Task.FromResult(Stocks.Values.Select(value => value.Stock).SingleOrDefault(value => value.Id == id));
    public Task EnsureStockAsync(Guid warehouse, Guid variant, CancellationToken ct)
    {
        Stocks.TryAdd((warehouse, variant), (Stock.Create(warehouse, variant), 0)); return Task.CompletedTask;
    }
    public Task<QuantityChange?> ChangeQuantityAsync(Guid warehouse, Guid variant, long delta, CancellationToken ct)
    {
        if (!Stocks.TryGetValue((warehouse, variant), out var item) || !item.Stock.IsActive ||
            !Warehouses.Any(value => value.Id == warehouse && value.IsActive) || item.Quantity + delta < 0 ||
            item.Quantity + delta > InventoryRules.MaximumQuantity) return Task.FromResult<QuantityChange?>(null);
        Stocks[(warehouse, variant)] = (item.Stock, item.Quantity + delta);
        return Task.FromResult<QuantityChange?>(new(item.Stock.Id, item.Quantity, item.Quantity + delta));
    }
    public Task<StoredOperation?> FindOperationAsync(Guid operationId, CancellationToken ct)
    {
        var movement = Movements.SingleOrDefault(value => value.OperationId == operationId);
        if (movement is null) return Task.FromResult<StoredOperation?>(null);
        var stock = Stocks.Values.Single(value => value.Stock.Id == movement.StockId).Stock;
        return Task.FromResult<StoredOperation?>(new(movement.RequestHash, new(movement.Id, movement.OperationId, stock.Id,
            stock.WarehouseId, stock.ProductVariantId, movement.QuantityDelta, movement.QuantityBefore, movement.QuantityAfter,
            movement.Type.ToString(), movement.Reference, movement.Reason, movement.ActorId, movement.CreatedAtUtc, movement.CorrelationId)));
    }
    public void AddMovement(InventoryMovement value) => Movements.Add(value);
    public void AddReceipt(StockReceipt value) => Receipts.Add(value);
    public void AddAdjustment(StockAdjustment value) => Adjustments.Add(value);
}
internal sealed class CommandHarness
{
    public MemoryStore Store { get; } = new();
    public VariantReferences Catalog { get; } = new();
    public Guid Actor { get; } = Guid.NewGuid();
    public Guid Warehouse { get; }
    public InventoryCommands Commands { get; }
    public CommandHarness()
    {
        var warehouse = Domain.Warehouse.Create("Main", "main", true); Store.Warehouses.Add(warehouse); Warehouse = warehouse.Id;
        Commands = new(new(Store, Store), new(Store, Store, Catalog, TimeProvider.System, new TestRequest(), NullLogger<StockCommands>.Instance));
    }
    public StockOperationInput Input(long quantity, Guid? operationId = null) => new()
    {
        OperationId = operationId ?? Guid.NewGuid(), WarehouseId = Warehouse, ProductVariantId = Catalog.Id,
        Quantity = quantity, Reference = "business-123", Reason = "Test operation"
    };
}
