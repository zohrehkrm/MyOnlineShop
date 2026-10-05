using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Pricing.Application;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Pricing.Domain;
using Xunit;

namespace MyOnlineShop.PricingDiscount.Tests;

public sealed class PricingTests
{
    [Fact]
    public async Task Commands_create_update_status_and_immutable_history()
    {
        using var h = new PriceHarness();
        var price = await h.Create();
        Assert.Equal(price, await h.Queries.GetCurrentAsync(h.Catalog.Id, " irr ", FixedClock.Now, default));
        await h.Commands.UpdateAsync(price.Id, h.Input(900_000m), h.Actor, default);
        Assert.Equal(900_000m, (await h.Queries.GetCurrentManyAsync([h.Catalog.Id], "IRR", FixedClock.Now, default)).Single().BasePrice);
        await h.Commands.SetActiveAsync(price.Id, false, h.Actor, default);
        Assert.Null(await h.Queries.GetCurrentAsync(h.Catalog.Id, "IRR", FixedClock.Now, default));
        await h.Commands.SetActiveAsync(price.Id, true, h.Actor, default);
        var history = await h.Queries.GetHistoryAsync(h.Catalog.Id, "IRR", 1, 20, default);
        Assert.Equal(4, history.Count);
        Assert.Equal(1_000_000m, history.Single(x => x.Action == "Created").Snapshot.BasePrice);
        Assert.All(history, x => { Assert.Equal(h.Actor, x.ActorId); Assert.Equal("pricing-test", x.CorrelationId); Assert.Equal(TimeSpan.Zero, x.AtUtc.Offset); });
        h.Context.History.Remove(await h.Context.History.FirstAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Context.SaveChangesAsync());
    }
    [Fact]
    public async Task Active_periods_cannot_overlap_including_activation_but_adjacent_periods_can()
    {
        using var h = new PriceHarness();
        var first = await h.Commands.CreateAsync(h.Input(to: FixedClock.Now), h.Actor, default);
        var second = await h.Commands.CreateAsync(h.Input(800_000m, from: FixedClock.Now), h.Actor, default);
        Assert.Equal(first.Id, (await h.Queries.GetCurrentAsync(h.Catalog.Id, "IRR", FixedClock.Now.AddTicks(-1), default))!.Id);
        Assert.Equal(second.Id, (await h.Queries.GetCurrentAsync(h.Catalog.Id, "IRR", FixedClock.Now, default))!.Id);
        var overlap = await Assert.ThrowsAsync<PricingException>(() => h.Commands.CreateAsync(h.Input(), h.Actor, default));
        Assert.Equal(409, overlap.StatusCode);
        var inactive = await h.Commands.CreateAsync(h.Input(active: false), h.Actor, default);
        Assert.Equal(409, (await Assert.ThrowsAsync<PricingException>(() => h.Commands.SetActiveAsync(inactive.Id, true, h.Actor, default))).StatusCode);
    }
    [Theory]
    [InlineData("IRR", 0)]
    [InlineData("IRR", -1)]
    [InlineData("IRR", 1.5)]
    [InlineData("USD", 1.001)]
    [InlineData("XYZ", 1)]
    [InlineData("", 1)]
    [InlineData("USD", 1000000000001)]
    public async Task Invalid_amount_currency_or_precision_is_rejected(string currency, decimal amount)
    {
        using var h = new PriceHarness();
        Assert.Equal(400, (await Assert.ThrowsAsync<PricingException>(() => h.Commands.CreateAsync(h.Input(amount, currency: currency), h.Actor, default))).StatusCode);
    }
    [Fact]
    public async Task Invalid_variant_dates_compare_price_and_identity_changes_are_rejected()
    {
        using var h = new PriceHarness();
        await Assert.ThrowsAsync<PricingException>(() => h.Commands.CreateAsync(h.Input(variant: Guid.NewGuid()), h.Actor, default));
        await Assert.ThrowsAsync<PricingException>(() => h.Commands.CreateAsync(h.Input(to: FixedClock.Now.AddDays(-2)), h.Actor, default));
        Assert.Throws<PriceRuleException>(() => VariantPrice.Create(new() { ProductVariantId = h.Catalog.Id, Currency = "IRR", BasePrice = 100, ComparePrice = 99, EffectiveFromUtc = FixedClock.Now }));
        Assert.Throws<PriceRuleException>(() => VariantPrice.Create(new() { ProductVariantId = h.Catalog.Id, Currency = "IRR", BasePrice = 100 }));
        var price = await h.Create();
        await Assert.ThrowsAsync<PricingException>(() => h.Commands.UpdateAsync(price.Id, h.Input(currency: "USD"), h.Actor, default));
        await Assert.ThrowsAsync<PricingException>(() => h.Commands.CreateAsync(h.Input(), Guid.Empty, default));
    }
    [Fact]
    public async Task Different_currencies_and_future_prices_are_independent()
    {
        using var h = new PriceHarness(); await h.Create();
        var usd = await h.Commands.CreateAsync(h.Input(10.25m, currency: "USD"), h.Actor, default);
        Assert.Equal(usd.Id, (await h.Queries.GetCurrentAsync(h.Catalog.Id, "USD", FixedClock.Now, default))!.Id);
        await h.Commands.CreateAsync(h.Input(100m, currency: "EUR", from: FixedClock.Now.AddDays(1)), h.Actor, default);
        Assert.Null(await h.Queries.GetCurrentAsync(h.Catalog.Id, "EUR", FixedClock.Now, default));
    }
}
