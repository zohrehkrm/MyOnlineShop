using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Cart.Infrastructure.Persistence;
using MyOnlineShop.Discount.Domain;
using MyOnlineShop.Discount.Infrastructure.Persistence;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using MyOnlineShop.Order.Application;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Contracts;
using Xunit;
using MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;

namespace MyOnlineShop.Order.Tests;

public sealed class CheckoutTests
{
    [Fact]
    public async Task Checkout_records_one_versioned_event_and_replay_does_not_enqueue_again()
    {
        using var h = new Harness(); await h.Seed(); await h.Add(); var input = h.Input();
        var order = await h.Checkout(input); var replay = await h.Checkout(input); Assert.Equal(order.Id, replay.Id);
        var message = await h.Services.GetRequiredService<MessagingDbContext>().Outbox.SingleAsync();
        Assert.Equal(order.Id, message.Id); Assert.Equal("orders.created", message.EventType); Assert.Equal(1, message.Version);
        Assert.Equal("Pending", message.Status); Assert.Equal("order-test", message.CorrelationId);
        Assert.DoesNotContain("Recipient", message.Payload); Assert.Contains(order.Id.ToString(), message.Payload);
    }
    [Fact]
    public async Task Checkout_reuses_currency_rounding_and_server_discount_calculation()
    {
        using var h = new Harness(); await h.Seed(); await h.Add(3);
        await h.Services.GetRequiredService<IPriceCommands>().CreateAsync(new()
        { ProductVariantId = h.Catalog.Id, Currency = "USD", BasePrice = 10.01m, EffectiveFromUtc = FixedClock.Now.AddDays(-1) }, Guid.NewGuid(), default);
        var discounts = h.Services.GetRequiredService<DiscountDbContext>();
        discounts.Rules.Add(DiscountRule.Create(new() { Name = "Rounded", Currency = "USD", Value = 33.3333m,
            StartsAtUtc = FixedClock.Now.AddDays(-1), EndsAtUtc = FixedClock.Now.AddDays(1) }, Guid.NewGuid(), FixedClock.Now));
        await discounts.SaveChangesAsync();
        var order = await h.Checkout(h.Input(currency: "USD"));
        Assert.Equal(30.03m, order.Subtotal); Assert.Equal(10.02m, order.DiscountTotal); Assert.Equal(20.01m, order.PayableAmount);
        Assert.Equal(3.34m, order.Items.Single().UnitDiscount); Assert.Equal(6.67m, order.Items.Single().FinalUnitPrice);
    }
    [Fact]
    public async Task Same_key_is_scoped_to_owner_and_multiple_checkouts_do_not_reserve_available_stock()
    {
        using var h = new Harness(); await h.Seed(); await h.Add(2);
        var input = h.Input(); var first = await h.Checkout(input); var other = Guid.NewGuid();
        await h.Services.GetRequiredService<ICartCommands>().AddAsync(other, new() { ProductVariantId = h.Catalog.Id, Quantity = 2 }, default);
        var second = await h.Services.GetRequiredService<ICheckoutCommands>().CreateAsync(other, input, default);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(5, (await h.Services.GetRequiredService<InventoryDbContext>().Stocks.AsNoTracking().SingleAsync()).Quantity);
        Assert.Empty(await h.Services.GetRequiredService<InventoryDbContext>().Movements.ToListAsync());
    }
    [Fact]
    public async Task Inactive_warehouse_stock_is_unavailable_and_cart_revision_check_preserves_new_items()
    {
        using var h = new Harness(); await h.Seed(); var cart = await h.Add();
        var inventory = h.Services.GetRequiredService<InventoryDbContext>();
        var warehouse = await inventory.Warehouses.SingleAsync();
        inventory.Entry(warehouse).Property(value => value.IsActive).CurrentValue = false; await inventory.SaveChangesAsync();
        await Assert.ThrowsAsync<OrderException>(() => h.Checkout());
        var checkoutCart = h.Services.GetRequiredService<ICheckoutCart>();
        var snapshot = await checkoutCart.GetAsync(h.User, default);
        await h.Add(1);
        await Assert.ThrowsAnyAsync<Exception>(() => checkoutCart.ClearAsync(h.User, cart.Id!.Value, snapshot.Revision, default));
        Assert.Equal(3, (await h.Services.GetRequiredService<ICartQueries>().GetCurrentAsync(h.User, default)).Items.Single().Quantity);
    }
    [Fact]
    public async Task Checkout_stock_five_quantity_two_snapshots_totals_and_clears_only_cart()
    {
        using var h = new Harness(); await h.Seed(); var cart = await h.Add();
        var discounts = h.Services.GetRequiredService<DiscountDbContext>();
        discounts.Rules.Add(DiscountRule.Create(new() { Name = "Offer", Type = "Fixed", Value = 100_000m, Currency = "IRR",
            StartsAtUtc = FixedClock.Now.AddDays(-1), EndsAtUtc = FixedClock.Now.AddDays(1) }, Guid.NewGuid(), FixedClock.Now));
        await discounts.SaveChangesAsync();
        var order = await h.Checkout();
        Assert.Equal("AwaitingPayment", order.Status); Assert.Equal(2_000_000m, order.Subtotal);
        Assert.Equal(200_000m, order.DiscountTotal); Assert.Equal(1_800_000m, order.PayableAmount);
        var item = Assert.Single(order.Items); Assert.Equal(2, item.Quantity); Assert.Equal("Original Product", item.ProductName);
        Assert.Equal("SKU-A", item.Sku); Assert.Equal(1_000_000m, item.UnitPrice); Assert.Equal(900_000m, item.FinalUnitPrice);
        Assert.Empty((await h.Services.GetRequiredService<ICartQueries>().GetCurrentAsync(h.User, default)).Items);
        Assert.Equal(cart.Id, (await h.Services.GetRequiredService<ICartQueries>().GetCurrentAsync(h.User, default)).Id);
        var inventory = h.Services.GetRequiredService<InventoryDbContext>();
        Assert.Equal(5, (await inventory.Stocks.AsNoTracking().SingleAsync()).Quantity);
        Assert.Empty(await inventory.Movements.ToListAsync());
        Assert.DoesNotContain(inventory.Model.GetEntityTypes(), value => value.Name.Contains("Reservation"));
        Assert.Single(await h.Services.GetRequiredService<OrderDbContext>().Audit.ToListAsync());
        h.Catalog.Name = "Renamed Product";
        var price = (await h.Services.GetRequiredService<IPriceQueries>().GetCurrentAsync(h.Catalog.Id, "IRR", FixedClock.Now, default))!;
        await h.Services.GetRequiredService<IPriceCommands>().UpdateAsync(price.Id, new()
        { ProductVariantId = h.Catalog.Id, BasePrice = 2_000_000m, Currency = "IRR", EffectiveFromUtc = FixedClock.Now.AddDays(-1) }, Guid.NewGuid(), default);
        var historical = await h.Services.GetRequiredService<IOrderQueries>().GetMyAsync(h.User, order.Id, default);
        Assert.Equal(order, historical with { Items = order.Items });
        Assert.Equal("Original Product", historical.Items.Single().ProductName); Assert.Equal(1_000_000m, historical.Items.Single().UnitPrice);
    }
    [Fact]
    public async Task Multiple_items_and_address_are_snapshotted_with_aggregate_totals()
    {
        using var h = new Harness(); await h.Seed(second: true); await h.Add(2); await h.Add(3, h.Catalog.SecondId);
        var input = h.Input(address: new() { Recipient = "Recipient", Street = "Street", City = "City", PostalCode = "12345", CountryCode = "ir" });
        var order = await h.Checkout(input);
        Assert.Equal(2, order.Items.Count); Assert.Equal(5_000_000m, order.Subtotal); Assert.Equal(0m, order.DiscountTotal);
        Assert.Equal(5_000_000m, order.PayableAmount); Assert.Equal("IR", order.Address!.CountryCode);
    }
    [Fact]
    public async Task Repeated_key_returns_persisted_result_without_clearing_new_cart_or_repricing()
    {
        using var h = new Harness(); await h.Seed(); await h.Add(); var input = h.Input(); var first = await h.Checkout(input);
        await h.Add(1); h.Catalog.Active = false; // Replay resolves before current validation.
        var replay = await h.Checkout(input);
        Assert.Equal(first.Id, replay.Id); Assert.Single(await h.Services.GetRequiredService<OrderDbContext>().Orders.ToListAsync());
        Assert.Equal(1, (await h.Services.GetRequiredService<ICartQueries>().GetCurrentAsync(h.User, default)).Items.Single().Quantity);
        Assert.Equal(409, (await Assert.ThrowsAsync<OrderException>(() => h.Checkout(h.Input(input.IdempotencyKey, coupon: "OTHER")))).StatusCode);
    }
    [Theory]
    [InlineData("Empty")]
    [InlineData("Inactive")]
    [InlineData("Missing")]
    [InlineData("NoPrice")]
    [InlineData("Stock")]
    [InlineData("Quantity")]
    [InlineData("Currency")]
    [InlineData("Key")]
    [InlineData("Coupon")]
    public async Task Failed_validation_leaves_cart_and_inventory_unchanged(string failure)
    {
        using var h = new Harness(); await h.Seed(price: failure != "NoPrice", stock: failure == "Stock" ? 1 : 5);
        CartDto? original = failure == "Empty" ? null : await h.Add();
        if (failure == "Inactive") h.Catalog.Active = false;
        if (failure == "Missing") h.Catalog.Exists = false;
        if (failure == "Quantity")
        {
            var db = h.Services.GetRequiredService<CartDbContext>(); var item = await db.Items.SingleAsync();
            db.Entry(item).Property(value => value.Quantity).CurrentValue = 0; await db.SaveChangesAsync();
        }
        var before = await h.Services.GetRequiredService<ICartQueries>().GetCurrentAsync(h.User, default);
        var input = h.Input(failure == "Key" ? Guid.Empty : null, failure == "Currency" ? "XYZ" : "IRR", failure == "Coupon" ? "Bad Code" : null);
        await Assert.ThrowsAnyAsync<Exception>(() => h.Checkout(input));
        Assert.Empty(await h.Services.GetRequiredService<OrderDbContext>().Orders.AsNoTracking().ToListAsync());
        var after = await h.Services.GetRequiredService<ICartQueries>().GetCurrentAsync(h.User, default);
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.Items.Select(x => (x.Id, x.Quantity)), after.Items.Select(x => (x.Id, x.Quantity)));
        Assert.Empty(await h.Services.GetRequiredService<InventoryDbContext>().Movements.ToListAsync());
    }
    [Fact]
    public async Task Digital_variant_does_not_require_warehouse_stock()
    {
        using var h = new Harness(); h.Catalog.Kind = "Digital"; await h.Seed(stock: 0); await h.Add();
        Assert.Equal("Digital", (await h.Checkout()).Items.Single().ProductKind);
    }
}
