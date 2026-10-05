using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Inventory.Application;
using MyOnlineShop.Inventory.Contracts;

namespace MyOnlineShop.Inventory.Presentation;

[ApiController, Route("api/v1/inventory")]
public sealed class InventoryController(IInventoryCommands commands, IInventoryQueries queries) : ControllerBase
{
    private Guid ActorId => Guid.TryParse(User.FindFirst("sub")?.Value, out var id) && id != Guid.Empty ? id :
        throw new InventoryException("inventory_actor", 401, "A valid authenticated actor is required.");
    private ApiResponse<T> Envelope<T>(T data) => new(data, HttpContext.TraceIdentifier);
    [Authorize(Policy = IdentityPermissions.ViewInventory), HttpGet("warehouses")]
    public async Task<ActionResult<ApiResponse<InventoryPage<WarehouseDto>>>> Warehouses([FromQuery] InventoryQuery query, CancellationToken ct) =>
        Ok(Envelope(await queries.ListWarehousesAsync(query, ct)));
    [Authorize(Policy = IdentityPermissions.ViewInventory), HttpGet("warehouses/{id:guid}")]
    public async Task<ActionResult<ApiResponse<WarehouseDto>>> Warehouse(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetWarehouseAsync(id, ct)));
    [Authorize(Policy = IdentityPermissions.AdjustInventory), HttpPost("warehouses")]
    public async Task<ActionResult<ApiResponse<WarehouseDto>>> CreateWarehouse(WarehouseInput input, CancellationToken ct)
    {
        var dto = await commands.CreateWarehouseAsync(input, ct);
        return CreatedAtAction(nameof(Warehouse), new { id = dto.Id }, Envelope(dto));
    }
    [Authorize(Policy = IdentityPermissions.AdjustInventory), HttpPut("warehouses/{id:guid}")]
    public async Task<ActionResult<ApiResponse<WarehouseDto>>> UpdateWarehouse(Guid id, WarehouseInput input, CancellationToken ct) =>
        Ok(Envelope(await commands.UpdateWarehouseAsync(id, input, ct)));
    [Authorize(Policy = IdentityPermissions.ViewInventory), HttpGet("stocks")]
    public async Task<ActionResult<ApiResponse<InventoryPage<StockDto>>>> Stocks([FromQuery] InventoryQuery query, CancellationToken ct) =>
        Ok(Envelope(await queries.ListStockAsync(query, ct)));
    [Authorize(Policy = IdentityPermissions.ViewInventory), HttpGet("warehouses/{warehouseId:guid}/variants/{variantId:guid}")]
    public async Task<ActionResult<ApiResponse<StockDto>>> Stock(Guid warehouseId, Guid variantId, CancellationToken ct) =>
        Ok(Envelope(await queries.GetStockAsync(warehouseId, variantId, ct)));
    [Authorize(Policy = IdentityPermissions.AdjustInventory), HttpPut("stocks/{stockId:guid}/settings")]
    public async Task<ActionResult<ApiResponse<StockDto>>> Configure(Guid stockId, StockSettingsInput input, CancellationToken ct) =>
        Ok(Envelope(await commands.ConfigureStockAsync(stockId, input, ct)));
    [Authorize(Policy = IdentityPermissions.ReceiveInventory), HttpPost("receipts")]
    public async Task<ActionResult<ApiResponse<MovementDto>>> Receive(StockOperationInput input, CancellationToken ct) =>
        Ok(Envelope(await commands.ReceiveAsync(input, ActorId, ct)));
    [Authorize(Policy = IdentityPermissions.ReceiveInventory), HttpPost("returns")]
    public async Task<ActionResult<ApiResponse<MovementDto>>> Return(StockOperationInput input, CancellationToken ct) =>
        Ok(Envelope(await commands.ReturnAsync(input, ActorId, ct)));
    [Authorize(Policy = IdentityPermissions.AdjustInventory), HttpPost("adjustments")]
    public async Task<ActionResult<ApiResponse<MovementDto>>> Adjust(StockAdjustmentInput input, CancellationToken ct) =>
        Ok(Envelope(await commands.AdjustAsync(input, ActorId, ct)));
    [Authorize(Policy = IdentityPermissions.ViewInventory), HttpGet("movements")]
    public async Task<ActionResult<ApiResponse<InventoryPage<MovementDto>>>> Movements([FromQuery] InventoryQuery query, CancellationToken ct) =>
        Ok(Envelope(await queries.ListMovementsAsync(query, ct)));
    [Authorize(Policy = IdentityPermissions.ViewInventory), HttpGet("receipts")]
    public async Task<ActionResult<ApiResponse<InventoryPage<ReceiptDto>>>> Receipts([FromQuery] InventoryQuery query, CancellationToken ct) =>
        Ok(Envelope(await queries.ListReceiptsAsync(query, ct)));
}
