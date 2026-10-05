using MyOnlineShop.Inventory.Application;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Inventory.Domain;
using Xunit;

namespace MyOnlineShop.Inventory.Tests;

public sealed class InventoryUnitTests
{
    [Fact]
    public async Task Receipt_and_return_record_positive_stock_changes_and_actor()
    {
        var harness = new CommandHarness();
        var received = await harness.Commands.ReceiveAsync(harness.Input(3), harness.Actor, default);
        var returned = await harness.Commands.ReturnAsync(harness.Input(2), harness.Actor, default);
        Assert.Equal(0, received.QuantityBefore); Assert.Equal(3, received.QuantityAfter);
        Assert.Equal(5, returned.QuantityAfter); Assert.Equal(harness.Actor, received.ActorId);
        Assert.Equal("inventory-test", received.CorrelationId);
        Assert.Single(harness.Store.Receipts); Assert.Equal(2, harness.Store.Movements.Count);
    }
    [Fact]
    public async Task Exact_deduction_succeeds_and_insufficient_stock_creates_no_movement()
    {
        var harness = new CommandHarness();
        await harness.Commands.ReceiveAsync(harness.Input(2), harness.Actor, default);
        await Assert.ThrowsAsync<InventoryException>(() => harness.Commands.DeductAsync(harness.Input(3), harness.Actor, default));
        Assert.Single(harness.Store.Movements);
        var movement = await harness.Commands.DeductAsync(harness.Input(2), harness.Actor, default);
        Assert.Equal(0, movement.QuantityAfter); Assert.Equal(-2, movement.QuantityDelta); Assert.Equal("Sale", movement.Type);
    }
    [Fact]
    public async Task Retried_operation_returns_original_movement_and_mismatched_reuse_conflicts()
    {
        var harness = new CommandHarness();
        await harness.Commands.ReceiveAsync(harness.Input(4), harness.Actor, default);
        var input = harness.Input(1);
        var original = await harness.Commands.DeductAsync(input, harness.Actor, default);
        var replay = await harness.Commands.DeductAsync(input, harness.Actor, default);
        Assert.Equal(original, replay); Assert.Equal(2, harness.Store.Movements.Count);
        Assert.Equal(3, harness.Store.Stocks.Values.Single().Quantity);
        await Assert.ThrowsAsync<InventoryException>(() => harness.Commands.DeductAsync(harness.Input(2, input.OperationId), harness.Actor, default));
        await Assert.ThrowsAsync<InventoryException>(() => harness.Commands.ReceiveAsync(input, harness.Actor, default));
    }
    [Fact]
    public async Task Adjustments_validate_bounds_and_record_reasons_and_damage()
    {
        var harness = new CommandHarness();
        await harness.Commands.ReceiveAsync(harness.Input(3), harness.Actor, default);
        StockAdjustmentInput Input(long delta, string type = "ManualAdjustment") => new()
        { OperationId = Guid.NewGuid(), WarehouseId = harness.Warehouse, ProductVariantId = harness.Catalog.Id,
            QuantityDelta = delta, Type = type, Reference = "count-1", Reason = "Physical count" };
        await harness.Commands.AdjustAsync(Input(-1, "Damage"), harness.Actor, default);
        await harness.Commands.AdjustAsync(Input(2), harness.Actor, default);
        await Assert.ThrowsAsync<InventoryException>(() => harness.Commands.AdjustAsync(Input(-5), harness.Actor, default));
        await Assert.ThrowsAsync<InventoryException>(() => harness.Commands.AdjustAsync(Input(0), harness.Actor, default));
        await Assert.ThrowsAsync<InventoryException>(() => harness.Commands.AdjustAsync(Input(1, "Damage"), harness.Actor, default));
        Assert.Equal(4, harness.Store.Stocks.Values.Single().Quantity); Assert.Equal(2, harness.Store.Adjustments.Count);
        Assert.Equal("Physical count", harness.Store.Movements.Last().Reason);
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(long.MinValue)] [InlineData(long.MaxValue)]
    public async Task Invalid_requested_quantities_are_rejected_before_mutating_stock(long quantity)
    {
        var harness = new CommandHarness();
        await Assert.ThrowsAsync<InventoryException>(() => harness.Commands.DeductAsync(harness.Input(quantity), harness.Actor, default));
        Assert.Empty(harness.Store.Movements);
    }
    [Fact]
    public async Task Missing_digital_inactive_variants_and_inactive_warehouses_are_validated()
    {
        var harness = new CommandHarness();
        harness.Catalog.Kind = "Digital";
        await Assert.ThrowsAsync<InventoryException>(() => harness.Commands.ReceiveAsync(harness.Input(1), harness.Actor, default));
        harness.Catalog.Kind = "Physical";
        await harness.Commands.ReceiveAsync(harness.Input(1), harness.Actor, default);
        harness.Catalog.Active = false;
        await Assert.ThrowsAsync<InventoryException>(() => harness.Commands.DeductAsync(harness.Input(1), harness.Actor, default));
        harness.Catalog.Active = true;
        harness.Store.Warehouses[0].Update("Main", "main", false);
        await Assert.ThrowsAsync<InventoryException>(() => harness.Commands.DeductAsync(harness.Input(1), harness.Actor, default));
        await Assert.ThrowsAsync<InventoryException>(() => harness.Commands.ReceiveAsync(new()
        { OperationId = Guid.NewGuid(), WarehouseId = harness.Warehouse, ProductVariantId = Guid.NewGuid(), Quantity = 1,
            Reference = "missing", Reason = "Missing reference" }, harness.Actor, default));
    }
    [Fact]
    public void Domain_and_pagination_reject_invalid_states()
    {
        Assert.Throws<InventoryRuleException>(() => InventoryRules.Apply(1, -2));
        Assert.Throws<InventoryRuleException>(() => InventoryRules.Apply(InventoryRules.MaximumQuantity, 1));
        Assert.Throws<InventoryRuleException>(() => Warehouse.Create("", "code", true));
        Assert.Throws<InventoryRuleException>(() => Stock.Create(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<InventoryException>(() => InventoryValidation.Validate(new InventoryQuery { Page = int.MaxValue, PageSize = 100 }));
        Assert.Throws<InventoryException>(() => InventoryValidation.Validate(new StockOperationInput
        { Quantity = 1, Reference = "reference", Reason = "reason" }));
    }
    [Fact]
    public async Task Inactive_stock_and_quantity_limit_reject_operations_without_extra_history()
    {
        var harness = new CommandHarness();
        await harness.Commands.ReceiveAsync(harness.Input(InventoryRules.MaximumQuantity), harness.Actor, default);
        await Assert.ThrowsAsync<InventoryException>(() => harness.Commands.ReceiveAsync(harness.Input(1), harness.Actor, default));
        var stock = harness.Store.Stocks.Values.Single().Stock;
        await harness.Commands.ConfigureStockAsync(stock.Id, new() { IsActive = false, LowStockThreshold = 5 }, default);
        await Assert.ThrowsAsync<InventoryException>(() => harness.Commands.DeductAsync(harness.Input(1), harness.Actor, default));
        Assert.Single(harness.Store.Movements);
        Assert.Throws<InventoryRuleException>(() => stock.Configure(true, -1));
    }
}
