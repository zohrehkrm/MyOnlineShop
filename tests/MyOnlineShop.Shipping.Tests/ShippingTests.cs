using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Shipping.Application;
using MyOnlineShop.Shipping.Contracts;
using Xunit;

namespace MyOnlineShop.Shipping.Tests;

public sealed class ShippingTests
{
    [Fact]
    public async Task Available_methods_filter_active_currency_and_support_arbitrary_codes()
    {
        using var h = new Harness(); var expected = await h.CreateMethod(Harness.Method("CUSTOM-DRONE"));
        await h.CreateMethod(Harness.Method("INACTIVE", active: false)); await h.CreateMethod(Harness.Method("USD", 5m, "USD"));
        Assert.Equal(expected.Id, Assert.Single(await h.Queries.AvailableMethodsAsync("irr", default)).Id);
        Assert.Equal(3, (await h.Queries.MethodsAsync(default)).Count);
        var error = await Assert.ThrowsAsync<ShippingException>(() => h.CreateMethod(Harness.Method("custom-drone"))); Assert.Equal(409, error.StatusCode);
    }
    [Fact]
    public async Task Cost_is_centralized_decimal_currency_checked_and_address_normalized()
    {
        using var h = new Harness(); var method = await h.CreateMethod(Harness.Method("CUSTOM", 10.25m, "USD"));
        var quote = await h.Quotes.CalculateAsync(new() { ShippingMethodId = method.Id, Currency = "USD", Address = Harness.Address(" Name ") }, default);
        Assert.Equal(10.25m, quote.Cost); Assert.Equal("Name", quote.Address.Recipient); Assert.Equal("IR", quote.Address.CountryCode);
        await Assert.ThrowsAsync<ShippingException>(() => h.Quotes.CalculateAsync(new() { ShippingMethodId = method.Id, Currency = "IRR", Address = Harness.Address() }, default));
        await Assert.ThrowsAsync<ShippingException>(() => h.CreateMethod(Harness.Method("NEGATIVE", -1m)));
        await Assert.ThrowsAsync<ShippingException>(() => h.CreateMethod(Harness.Method("PRECISION", 1.5m)));
        var free = await h.CreateMethod(Harness.Method("FREE-COLLECT", 0m, tracking: false));
        Assert.Equal(0m, (await h.Quotes.CalculateAsync(new() { ShippingMethodId = free.Id, Currency = "IRR", Address = Harness.Address() }, default)).Cost);
    }
    [Fact]
    public async Task Checkout_adds_server_shipping_cost_and_preserves_selection_after_method_changes()
    {
        using var h = new Harness(); var method = await h.CreateMethod(); var address = Harness.Address(); var key = Guid.NewGuid();
        var order = await h.Checkout(method, address, key); Assert.Equal(50_000m, order.ShippingCost); Assert.Equal(2_050_000m, order.PayableAmount);
        Assert.Equal(order.Subtotal - order.DiscountTotal + order.ShippingCost, order.PayableAmount); Assert.Equal("Original Recipient", order.Shipping!.Address.Recipient);
        await h.Commands.UpdateMethodAsync(h.Actor, method.Id, new() { ExpectedRevision = method.Revision, Method = Harness.Method(cost: 90_000m) }, default);
        var replay = await h.Order.Checkout(new() { IdempotencyKey = key, Currency = "IRR", Shipping = new() { ShippingMethodId = method.Id, Address = address } });
        Assert.Equal(order.Id, replay.Id); Assert.Equal(50_000m, replay.ShippingCost);
        await h.Paid(order.Id); var shipment = await h.Commands.CreateShipmentAsync(h.Actor, order.Id, default);
        Assert.Equal(50_000m, shipment.ShippingCost); Assert.Equal(order.Shipping.Address, shipment.Address);
        Assert.Equal(order.Id, shipment.OrderId); Assert.Equal("Pending", shipment.Status);
        Assert.Equal(shipment.Id, (await h.Commands.CreateShipmentAsync(h.Actor, order.Id, default)).Id);
        Assert.Single(await h.Db.Shipments.ToListAsync());
    }
    [Fact]
    public async Task Changed_shipping_selection_conflicts_with_checkout_key()
    {
        using var h = new Harness(); var method = await h.CreateMethod(); var key = Guid.NewGuid(); await h.Checkout(method, key: key);
        var error = await Assert.ThrowsAsync<MyOnlineShop.Order.Application.OrderException>(() => h.Order.Checkout(new()
        { IdempotencyKey = key, Currency = "IRR", Shipping = new() { ShippingMethodId = method.Id, Address = Harness.Address("Other") } }));
        Assert.Equal(409, error.StatusCode);
    }
    [Fact]
    public async Task User_isolation_returns_not_found_for_other_users_orders()
    {
        using var h = new Harness(); var shipment = await h.Shipment();
        Assert.Equal(shipment.Id, (await h.Queries.GetMyOrderAsync(h.Order.User, shipment.OrderId, default)).Id);
        Assert.Equal(404, (await Assert.ThrowsAsync<ShippingException>(() => h.Queries.GetMyOrderAsync(Guid.NewGuid(), shipment.OrderId, default))).StatusCode);
        Assert.Equal(401, (await Assert.ThrowsAsync<ShippingException>(() => h.Queries.GetMyOrderAsync(Guid.Empty, shipment.OrderId, default))).StatusCode);
    }
    [Fact]
    public async Task Creation_rejects_unpaid_missing_digital_or_legacy_unselected_orders()
    {
        using var h = new Harness(); var method = await h.CreateMethod(); var order = await h.Checkout(method);
        await Assert.ThrowsAsync<ShippingException>(() => h.Commands.CreateShipmentAsync(h.Actor, order.Id, default));
        await Assert.ThrowsAsync<ShippingException>(() => h.Commands.CreateShipmentAsync(h.Actor, Guid.NewGuid(), default));
        using var legacy = new Harness(); await legacy.Order.Seed(); await legacy.Order.Add(); var old = await legacy.Order.Checkout(); await legacy.Paid(old.Id);
        await Assert.ThrowsAsync<ShippingException>(() => legacy.Commands.CreateShipmentAsync(legacy.Actor, old.Id, default));
        using var digital = new Harness(); var digitalMethod = await digital.CreateMethod(); digital.Order.Catalog.Kind = "Digital";
        await digital.Order.Seed(); await digital.Order.Add();
        await Assert.ThrowsAsync<MyOnlineShop.Order.Application.OrderException>(() => digital.Order.Checkout(new()
        { IdempotencyKey = Guid.NewGuid(), Currency = "IRR", Shipping = new() { ShippingMethodId = digitalMethod.Id, Address = Harness.Address() } }));
    }
    [Fact]
    public async Task Inactive_method_cannot_quote_or_create_shipment()
    {
        using var h = new Harness(); var inactive = await h.CreateMethod(Harness.Method(active: false));
        await Assert.ThrowsAsync<ShippingException>(() => h.Quotes.CalculateAsync(new() { ShippingMethodId = inactive.Id, Currency = "IRR", Address = Harness.Address() }, default));
        var method = await h.CreateMethod(Harness.Method("ACTIVE")); var order = await h.Checkout(method); await h.Paid(order.Id);
        await h.Commands.UpdateMethodAsync(h.Actor, method.Id, new() { ExpectedRevision = method.Revision, Method = Harness.Method("ACTIVE", active: false) }, default);
        await Assert.ThrowsAsync<ShippingException>(() => h.Commands.CreateShipmentAsync(h.Actor, order.Id, default));
    }
    [Fact]
    public async Task Status_machine_tracking_and_timestamps_follow_controlled_progression()
    {
        using var h = new Harness(); var shipment = await h.Shipment();
        await Assert.ThrowsAsync<ShippingException>(() => h.Status(shipment, "Delivered"));
        shipment = await h.Status(shipment, "Preparing"); await Assert.ThrowsAsync<ShippingException>(() => h.Status(shipment, "Shipped"));
        shipment = await h.Commands.AssignTrackingAsync(h.Actor, shipment.Id, new() { ExpectedRevision = shipment.Revision, TrackingNumber = " TRACK-1 ", Carrier = " Carrier " }, default);
        Assert.Equal("TRACK-1", shipment.TrackingNumber); Assert.Equal("Carrier", shipment.Carrier);
        shipment = await h.Status(shipment, "Shipped"); Assert.NotNull(shipment.ShippedAtUtc);
        await Assert.ThrowsAsync<ShippingException>(() => h.Commands.AssignTrackingAsync(h.Actor, shipment.Id,
            new() { ExpectedRevision = shipment.Revision, TrackingNumber = "Changed", Carrier = "Changed" }, default));
        shipment = await h.Status(shipment, "InTransit"); shipment = await h.Status(shipment, "Delivered"); Assert.NotNull(shipment.DeliveredAtUtc);
        await Assert.ThrowsAsync<ShippingException>(() => h.Status(shipment, "Pending"));
        await Assert.ThrowsAsync<ShippingException>(() => h.Status(shipment, "Shipped"));
    }
    [Fact]
    public async Task Duplicate_and_stale_updates_are_safe_and_do_not_append_duplicate_audit()
    {
        using var h = new Harness(); var original = await h.Shipment(); var preparing = await h.Status(original, "Preparing");
        var error = await Assert.ThrowsAsync<ShippingException>(() => h.Status(original, "Preparing")); Assert.Equal(409, error.StatusCode);
        var auditCount = await h.Db.Audit.CountAsync(); var unchanged = await h.Status(preparing, "Preparing");
        Assert.Equal(preparing.Revision, unchanged.Revision); Assert.Equal(auditCount, await h.Db.Audit.CountAsync());
        await Assert.ThrowsAsync<ShippingException>(() => h.Status(preparing, "999"));
    }
    [Fact]
    public async Task Cancellation_is_terminal_and_tracking_optional_by_method_configuration()
    {
        using var h = new Harness(); var shipment = await h.Shipment(); shipment = await h.Status(shipment, "Cancelled");
        await Assert.ThrowsAsync<ShippingException>(() => h.Status(shipment, "Delivered"));
        using var pickup = new Harness(); var method = await pickup.CreateMethod(Harness.Method("COLLECT", 0m, tracking: false));
        var collected = await pickup.Shipment(method); collected = await pickup.Status(collected, "Preparing");
        collected = await pickup.Status(collected, "Shipped"); Assert.Null(collected.TrackingNumber);
    }
    [Fact]
    public async Task Historical_shipment_address_cost_order_and_audit_are_immutable()
    {
        using var h = new Harness(); var dto = await h.Shipment(); var shipment = await h.Db.Shipments.SingleAsync();
        h.Db.Entry(shipment).Property(value => value.ShippingCost).CurrentValue = 1m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync()); h.Db.ChangeTracker.Clear();
        shipment = await h.Db.Shipments.SingleAsync(); h.Db.Entry(shipment.Address).Property(value => value.Street).CurrentValue = "Changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync()); h.Db.ChangeTracker.Clear();
        var audit = await h.Db.Audit.FirstAsync(); h.Db.Audit.Remove(audit); await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync());
        h.Db.ChangeTracker.Clear(); Assert.Equal("Original street", (await h.Queries.GetAsync(dto.Id, default)).Address.Street);
    }
    [Theory]
    [InlineData("phone")][InlineData("state")][InlineData("postal")][InlineData("recipient")]
    public async Task Invalid_address_is_rejected(string field)
    {
        using var h = new Harness(); var method = await h.CreateMethod(); var valid = Harness.Address();
        var input = new ShippingAddressInput { Recipient = field == "recipient" ? " " : valid.Recipient, PhoneNumber = field == "phone" ? "123" : valid.PhoneNumber,
            State = field == "state" ? "" : valid.State, City = valid.City, Street = valid.Street, PostalCode = field == "postal" ? "" : valid.PostalCode, CountryCode = valid.CountryCode };
        await Assert.ThrowsAsync<ShippingException>(() => h.Quotes.CalculateAsync(new() { ShippingMethodId = method.Id, Currency = "IRR", Address = input }, default));
    }
}
