using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Order.Application;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Order.Domain;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Contracts;
using Xunit;
using OrderAggregate = MyOnlineShop.Order.Domain.Order;

namespace MyOnlineShop.Order.Tests;

public sealed class OrderTests
{
    internal static OrderAggregate Create()
    {
        var variant = Guid.NewGuid();
        var quote = new PriceLineQuote(variant, 2, Guid.NewGuid(), "IRR", 1_000_000m, null, null, 0, 0, 1_000_000m, 2_000_000m, []);
        return OrderAggregate.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('A', 64),
            "IRR", FixedClock.Now, FixedClock.Now, [OrderItem.Snapshot(variant, "SKU", "Product", "Physical", quote, "IRR")], null);
    }
    [Fact]
    public void Explicit_lifecycle_allows_only_documented_edges_and_terminal_states()
    {
        var order = Create(); Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Throws<OrderRuleException>(() => order.Transition(OrderStatus.Paid, FixedClock.Now));
        foreach (var state in new[] { OrderStatus.AwaitingPayment, OrderStatus.Paid, OrderStatus.Processing, OrderStatus.Shipped, OrderStatus.Completed })
            order.Transition(state, FixedClock.Now);
        foreach (var state in Enum.GetValues<OrderStatus>()) Assert.Throws<OrderRuleException>(() => order.Transition(state, FixedClock.Now));
        foreach (var terminal in new[] { OrderStatus.Cancelled, OrderStatus.Failed })
        {
            var unpaid = Create(); unpaid.Transition(OrderStatus.AwaitingPayment, FixedClock.Now); unpaid.Transition(terminal, FixedClock.Now);
            Assert.Throws<OrderRuleException>(() => unpaid.Transition(OrderStatus.AwaitingPayment, FixedClock.Now));
        }
    }
    [Fact]
    public async Task Own_orders_queries_cancellation_and_invalid_transitions_preserve_ownership()
    {
        using var h = new Harness(); await h.Seed(); await h.Add(); var order = await h.Checkout();
        var queries = h.Services.GetRequiredService<IOrderQueries>(); var commands = h.Services.GetRequiredService<IOrderCommands>();
        Assert.Single((await queries.ListMyAsync(h.User, 1, 20, default)).Items);
        var other = Guid.NewGuid(); Assert.Empty((await queries.ListMyAsync(other, 1, 20, default)).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<OrderException>(() => queries.GetMyAsync(other, order.Id, default))).StatusCode);
        await Assert.ThrowsAsync<OrderException>(() => commands.CancelAsync(other, order.Id, default));
        await Assert.ThrowsAsync<OrderException>(() => commands.ChangeStatusAsync(h.User, order.Id, "Paid", default));
        await Assert.ThrowsAsync<OrderException>(() => commands.ChangeStatusAsync(h.User, order.Id, "Shipped", default));
        Assert.Equal("Cancelled", (await commands.CancelAsync(h.User, order.Id, default)).Status);
        await Assert.ThrowsAsync<OrderException>(() => commands.CancelAsync(h.User, order.Id, default));
        Assert.Equal(2, await h.Services.GetRequiredService<OrderDbContext>().Audit.CountAsync());
    }
    [Fact]
    public async Task Paid_cancellation_is_rejected_and_persisted_snapshots_cannot_be_edited()
    {
        using var h = new Harness(); await h.Seed(); await h.Add(); var order = await h.Checkout();
        var db = h.Services.GetRequiredService<OrderDbContext>(); db.ChangeTracker.Clear();
        var entity = await db.Orders.Include(value => value.Items).SingleAsync();
        entity.Transition(OrderStatus.Paid, FixedClock.Now); await db.SaveChangesAsync(); // Domain-only fixture for future verified payment.
        await Assert.ThrowsAsync<OrderException>(() => h.Services.GetRequiredService<IOrderCommands>().CancelAsync(h.User, order.Id, default));
        db.ChangeTracker.Clear();
        var item = await db.Items.SingleAsync(); db.Entry(item).Property(value => value.UnitPrice).CurrentValue = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear(); var audit = await db.Audit.FirstAsync(); db.Audit.Remove(audit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
    [Fact]
    public void Inconsistent_or_negative_pricing_snapshot_is_rejected()
    {
        var id = Guid.NewGuid();
        var quote = new PriceLineQuote(id, 1, Guid.NewGuid(), "IRR", 100, null, Guid.NewGuid(), 20, 20, 80, 80, []);
        Assert.Throws<OrderRuleException>(() => OrderItem.Snapshot(id, "SKU", "Product", "Physical", quote with { FinalUnitPrice = -1 }, "IRR"));
        Assert.Throws<OrderRuleException>(() => OrderItem.Snapshot(id, "SKU", "Product", "Physical", quote with { DiscountAmount = 0 }, "IRR"));
        Assert.Throws<OrderRuleException>(() => OrderItem.Snapshot(id, "SKU", "Product", "Physical", quote with { TotalLineAmount = 100 }, "IRR"));
        Assert.Throws<MoneyRuleException>(() => OrderItem.Snapshot(id, "SKU", "Product", "Physical", quote with { BaseUnitPrice = 0 }, "IRR"));
    }
}
