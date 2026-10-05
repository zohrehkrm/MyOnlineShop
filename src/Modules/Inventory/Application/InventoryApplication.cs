using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Inventory.Domain;

namespace MyOnlineShop.Inventory.Application;

public sealed class InventoryException(string code, int statusCode, string message) : Exception(message), IApplicationError
{
    public string Code => code;
    public int StatusCode => statusCode;
    public string SafeMessage => Message;
    public static InventoryException Invalid(string message = "Inventory input is invalid.") => new("inventory_validation", 400, message);
    public static InventoryException NotFound() => new("inventory_not_found", 404, "Inventory resource not found.");
    public static InventoryException Conflict(string message) => new("inventory_conflict", 409, message);
}
public static class InventoryValidation
{
    public static void Validate(object input)
    {
        if (!Validator.TryValidateObject(input, new ValidationContext(input), [], true)) throw InventoryException.Invalid();
        if (input is InventoryQuery query && (((long)query.Page - 1) * query.PageSize > int.MaxValue ||
            query.WarehouseId == Guid.Empty || query.ProductVariantId == Guid.Empty)) throw InventoryException.Invalid();
        if (input is StockOperationInput operation)
            Check(operation.OperationId, operation.WarehouseId, operation.ProductVariantId);
        if (input is StockAdjustmentInput adjustment)
        {
            Check(adjustment.OperationId, adjustment.WarehouseId, adjustment.ProductVariantId);
            if (adjustment.QuantityDelta == 0 || (adjustment.Type == "Damage" && adjustment.QuantityDelta > 0))
                throw InventoryException.Invalid("Adjustment must be nonzero; damage must reduce stock.");
        }
    }
    private static void Check(Guid operation, Guid warehouse, Guid variant)
    { if (operation == Guid.Empty || warehouse == Guid.Empty || variant == Guid.Empty) throw InventoryException.Invalid(); }
}
public sealed record QuantityChange(Guid StockId, long Before, long After);
public sealed record StoredOperation(string RequestHash, MovementDto Movement);
public interface IInventoryUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
    Task LockAsync(string resource, CancellationToken ct);
}
public interface IInventoryStore
{
    Task<Warehouse?> GetWarehouseAsync(Guid id, CancellationToken ct);
    Task<bool> WarehouseCodeExistsAsync(string code, Guid? excludingId, CancellationToken ct);
    void AddWarehouse(Warehouse warehouse);
    Task<Stock?> GetStockAsync(Guid id, CancellationToken ct);
    Task EnsureStockAsync(Guid warehouseId, Guid variantId, CancellationToken ct);
    Task<QuantityChange?> ChangeQuantityAsync(Guid warehouseId, Guid variantId, long delta, CancellationToken ct);
    Task<StoredOperation?> FindOperationAsync(Guid operationId, CancellationToken ct);
    void AddMovement(InventoryMovement movement);
    void AddReceipt(StockReceipt receipt);
    void AddAdjustment(StockAdjustment adjustment);
}
public interface IInventoryReadStore : IInventoryQueries;

public sealed class WarehouseCommands(IInventoryStore store, IInventoryUnitOfWork unit)
{
    public async Task<WarehouseDto> SaveAsync(Guid? id, WarehouseInput input, CancellationToken ct)
    {
        InventoryValidation.Validate(input);
        return await unit.ExecuteAsync(async token =>
        {
            var warehouse = id is null ? Warehouse.Create(input.Name, input.Code, input.IsActive) :
                await store.GetWarehouseAsync(id.Value, token) ?? throw InventoryException.NotFound();
            warehouse.Update(input.Name, input.Code, input.IsActive);
            if (await store.WarehouseCodeExistsAsync(warehouse.Code, id, token))
                throw InventoryException.Conflict("Warehouse code already exists.");
            if (id is null) store.AddWarehouse(warehouse);
            return new WarehouseDto(warehouse.Id, warehouse.Name, warehouse.Code, warehouse.IsActive);
        }, ct);
    }
    public async Task<StockDto> ConfigureAsync(Guid id, StockSettingsInput input, CancellationToken ct)
    {
        InventoryValidation.Validate(input);
        return await unit.ExecuteAsync(async token =>
        {
            var stock = await store.GetStockAsync(id, token) ?? throw InventoryException.NotFound();
            stock.Configure(input.IsActive, input.LowStockThreshold);
            var warehouse = await store.GetWarehouseAsync(stock.WarehouseId, token) ?? throw InventoryException.NotFound();
            return new StockDto(stock.Id, stock.WarehouseId, stock.ProductVariantId, stock.Quantity,
                stock.IsActive && warehouse.IsActive ? stock.Quantity : 0, stock.LowStockThreshold, stock.IsActive);
        }, ct);
    }
}

public sealed class StockCommands(IInventoryStore store, IInventoryUnitOfWork unit, ICatalogVariantReferences catalog,
    TimeProvider clock, IRequestContext request, ILogger<StockCommands> logger)
{
    public async Task<MovementDto> ExecuteAsync(Guid operationId, Guid warehouseId, Guid variantId, long delta,
        MovementType type, string reference, string reason, Guid actorId, CancellationToken ct)
    {
        if (operationId == Guid.Empty || warehouseId == Guid.Empty || variantId == Guid.Empty || actorId == Guid.Empty)
            throw InventoryException.Invalid();
        InventoryRules.ValidateDelta(delta, type);
        reference = InventoryRules.Required(reference, 200, "Business reference");
        reason = InventoryRules.Required(reason, 500, "Reason");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { WarehouseId = warehouseId, VariantId = variantId, Delta = delta, Type = type, Reference = reference, Reason = reason, ActorId = actorId })));
        var result = await unit.ExecuteAsync(async token =>
        {
            await unit.LockAsync($"InventoryOperation:{operationId:N}", token);
            var previous = await store.FindOperationAsync(operationId, token);
            if (previous is not null)
            {
                if (previous.RequestHash != hash) throw InventoryException.Conflict("Operation ID was already used with a different request.");
                return previous.Movement;
            }
            var variant = await catalog.GetAsync(variantId, token);
            if (variant is null) throw InventoryException.Invalid("Catalog variant does not exist.");
            if (variant.ProductKind != "Physical") throw InventoryException.Invalid("Physical warehouse stock requires a physical Catalog product.");
            if (type == MovementType.Sale && !variant.IsActive) throw InventoryException.Conflict("Catalog variant is not available for deduction.");
            if (delta > 0)
            {
                var warehouse = await store.GetWarehouseAsync(warehouseId, token) ?? throw InventoryException.NotFound();
                if (!warehouse.IsActive) throw InventoryException.Conflict("Warehouse is inactive.");
                // This narrow lock protects creation only; deductions use the conditional SQL UPDATE.
                await unit.LockAsync($"InventoryStockCreation:{warehouseId:N}:{variantId:N}", token);
                await store.EnsureStockAsync(warehouseId, variantId, token);
            }
            var change = await store.ChangeQuantityAsync(warehouseId, variantId, delta, token);
            if (change is null) throw InventoryException.Conflict("Insufficient stock, inactive inventory/warehouse or quantity limit exceeded.");
            var movement = InventoryMovement.Create(operationId, change.StockId, delta, change.Before, change.After,
                type, reference, reason, actorId, hash, request.CorrelationId, clock.GetUtcNow());
            store.AddMovement(movement);
            if (type == MovementType.Receipt) store.AddReceipt(StockReceipt.From(movement));
            if (type is MovementType.ManualAdjustment or MovementType.Damage) store.AddAdjustment(StockAdjustment.From(movement));
            return new MovementDto(movement.Id, operationId, change.StockId, warehouseId, variantId, delta,
                change.Before, change.After, type.ToString(), reference, reason, actorId, movement.CreatedAtUtc, movement.CorrelationId);
        }, ct);
        logger.LogInformation("Inventory operation completed {OperationId} {InventoryMovementId} {WarehouseId} {ProductVariantId} {MovementType}",
            operationId, result.Id, warehouseId, variantId, type);
        return result;
    }
}

public sealed class InventoryCommands(WarehouseCommands warehouses, StockCommands stock) : IInventoryCommands, IInventoryDeduction
{
    public Task<WarehouseDto> CreateWarehouseAsync(WarehouseInput input, CancellationToken ct) => warehouses.SaveAsync(null, input, ct);
    public Task<WarehouseDto> UpdateWarehouseAsync(Guid id, WarehouseInput input, CancellationToken ct) => warehouses.SaveAsync(id, input, ct);
    public Task<StockDto> ConfigureStockAsync(Guid id, StockSettingsInput input, CancellationToken ct) => warehouses.ConfigureAsync(id, input, ct);
    public Task<MovementDto> ReceiveAsync(StockOperationInput input, Guid actorId, CancellationToken ct) => Positive(input, actorId, MovementType.Receipt, ct);
    public Task<MovementDto> ReturnAsync(StockOperationInput input, Guid actorId, CancellationToken ct) => Positive(input, actorId, MovementType.Return, ct);
    public Task<MovementDto> DeductAsync(StockOperationInput input, Guid actorId, CancellationToken ct)
    {
        InventoryValidation.Validate(input);
        return stock.ExecuteAsync(input.OperationId, input.WarehouseId, input.ProductVariantId, -input.Quantity,
            MovementType.Sale, input.Reference, input.Reason, actorId, ct);
    }
    private Task<MovementDto> Positive(StockOperationInput input, Guid actorId, MovementType type, CancellationToken ct)
    {
        InventoryValidation.Validate(input);
        return stock.ExecuteAsync(input.OperationId, input.WarehouseId, input.ProductVariantId, input.Quantity,
            type, input.Reference, input.Reason, actorId, ct);
    }
    public Task<MovementDto> AdjustAsync(StockAdjustmentInput input, Guid actorId, CancellationToken ct)
    {
        InventoryValidation.Validate(input);
        return stock.ExecuteAsync(input.OperationId, input.WarehouseId, input.ProductVariantId, input.QuantityDelta,
            Enum.Parse<MovementType>(input.Type), input.Reference, input.Reason, actorId, ct);
    }
}

public sealed class InventoryQueries(IInventoryReadStore store) : IInventoryQueries
{
    public Task<WarehouseDto> GetWarehouseAsync(Guid id, CancellationToken ct) => store.GetWarehouseAsync(id, ct);
    public Task<StockDto> GetStockAsync(Guid warehouseId, Guid variantId, CancellationToken ct) => store.GetStockAsync(warehouseId, variantId, ct);
    public Task<InventoryPage<WarehouseDto>> ListWarehousesAsync(InventoryQuery query, CancellationToken ct)
    { InventoryValidation.Validate(query); return store.ListWarehousesAsync(query, ct); }
    public Task<InventoryPage<StockDto>> ListStockAsync(InventoryQuery query, CancellationToken ct)
    { InventoryValidation.Validate(query); return store.ListStockAsync(query, ct); }
    public Task<InventoryPage<MovementDto>> ListMovementsAsync(InventoryQuery query, CancellationToken ct)
    { InventoryValidation.Validate(query); return store.ListMovementsAsync(query, ct); }
    public Task<InventoryPage<ReceiptDto>> ListReceiptsAsync(InventoryQuery query, CancellationToken ct)
    { InventoryValidation.Validate(query); return store.ListReceiptsAsync(query, ct); }
}
