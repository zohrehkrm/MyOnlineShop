using MyOnlineShop.Discount.Contracts;
using MyOnlineShop.Discount.Domain;
using MyOnlineShop.Pricing.Application;
using MyOnlineShop.Pricing.Contracts;
using Xunit;

namespace MyOnlineShop.PricingDiscount.Tests;

public sealed class DiscountCalculationTests
{
    internal static DiscountRuleDto Rule(string type = "Percentage", decimal value = 20m) =>
        new(Guid.NewGuid(), "Offer", type, value, "IRR", true, FixedClock.Now.AddDays(-1), FixedClock.Now.AddDays(1), 0, 0m, null, 0, null, null, null, null);
    [Theory]
    [InlineData("Percentage", 20, 800000)]
    [InlineData("Fixed", 150000, 850000)]
    [InlineData("Fixed", 1500000, 0)]
    [InlineData("Percentage", 100, 0)]
    public async Task Server_calculates_unit_and_quantity_totals_with_floor_at_zero(string type, decimal value, decimal expected)
    {
        using var h = new PriceHarness(); await h.Create(); h.Discounts.Rules.Add(Rule(type, value));
        var line = Assert.Single((await h.Quote(3)).Lines);
        Assert.Equal(expected, line.FinalUnitPrice); Assert.Equal(expected * 3, line.TotalLineAmount);
        Assert.Equal((1_000_000m - expected) * 3, line.DiscountAmount);
    }
    [Fact]
    public async Task Inactive_expired_future_wrong_currency_and_exhausted_rules_are_ignored()
    {
        using var h = new PriceHarness(); await h.Create();
        h.Discounts.Rules.AddRange([Rule() with { IsActive = false }, Rule() with { EndsAtUtc = FixedClock.Now },
            Rule() with { StartsAtUtc = FixedClock.Now.AddTicks(1) }, Rule() with { Currency = "USD" }, Rule() with { UsageLimit = 1, UsedCount = 1 }]);
        var line = (await h.Quote()).Lines.Single(); Assert.Null(line.DiscountId); Assert.Empty(line.ApplicableDiscounts); Assert.Equal(1_000_000m, line.FinalUnitPrice);
    }
    [Fact]
    public async Task Priority_then_saving_then_identifier_is_deterministic_without_stacking()
    {
        using var h = new PriceHarness(); await h.Create();
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        h.Discounts.Rules.AddRange([Rule(value: 90), Rule(value: 10) with { Priority = 1 },
            Rule(value: 20) with { Priority = 1 }, Rule(value: 20) with { Priority = 1, Id = id }]);
        var line = (await h.Quote()).Lines.Single(); Assert.Equal(id, line.DiscountId); Assert.Equal(800_000m, line.FinalUnitPrice);
        h.Discounts.Rules.Reverse(); Assert.Equal(id, (await h.Quote()).Lines.Single().DiscountId);
    }
    [Fact]
    public async Task Server_subtotal_coupon_and_target_eligibility_are_enforced_without_consuming_usage()
    {
        using var h = new PriceHarness(); await h.Create();
        var rule = Rule() with { MinimumOrderAmount = 2_000_000m, CouponCode = "SALE", UsageLimit = 1, ProductVariantId = h.Catalog.Id };
        h.Discounts.Rules.Add(rule);
        Assert.Null((await h.Quote(1, "SALE")).Lines.Single().DiscountId);
        Assert.Null((await h.Quote(2)).Lines.Single().DiscountId);
        Assert.Equal(rule.Id, (await h.Quote(2, " sale ")).Lines.Single().DiscountId);
        Assert.Equal(rule.Id, (await h.Quote(2, "SALE")).Lines.Single().DiscountId);
        Assert.Equal(0, h.Discounts.Rules.Single().UsedCount);
        foreach (var target in new[] { rule with { ProductVariantId = Guid.NewGuid() }, rule with { ProductVariantId = null, ProductId = Guid.NewGuid() }, rule with { ProductVariantId = null, CategoryId = Guid.NewGuid() } })
        { h.Discounts.Rules.Clear(); h.Discounts.Rules.Add(target); Assert.Null((await h.Quote(2, "SALE")).Lines.Single().DiscountId); }
        foreach (var target in new[] { rule with { ProductVariantId = null, ProductId = h.Catalog.ProductId }, rule with { ProductVariantId = null, CategoryId = h.Catalog.CategoryId } })
        { h.Discounts.Rules.Clear(); h.Discounts.Rules.Add(target); Assert.Equal(target.Id, (await h.Quote(2, "SALE")).Lines.Single().DiscountId); }
    }
    [Fact]
    public async Task Quantity_missing_prices_inactive_variants_and_bad_coupons_fail_safely()
    {
        using var h = new PriceHarness();
        Assert.Equal(404, (await Assert.ThrowsAsync<PricingException>(() => h.Quote())).StatusCode);
        await h.Create();
        await Assert.ThrowsAsync<PricingException>(() => h.Quote(0));
        await Assert.ThrowsAsync<PricingException>(() => h.Quote(1000));
        await Assert.ThrowsAsync<PricingException>(() => h.Quote(coupon: "Bad Code"));
        await Assert.ThrowsAsync<PricingException>(() => h.Calculation.CalculateAsync([new(h.Catalog.Id, 1), new(h.Catalog.Id, 2)], "IRR", null, default));
        h.Catalog.Active = false; await Assert.ThrowsAsync<PricingException>(() => h.Quote());
    }
    [Theory]
    [InlineData("IRR", 105, 10, 11)]
    [InlineData("USD", 0.05, 10, 0.01)]
    [InlineData("USD", 10.01, 33.3333, 3.34)]
    public void Currency_rounding_is_explicit_and_uses_decimal(string currency, decimal price, decimal percent, decimal expected) =>
        Assert.Equal(expected, DiscountCalculation.UnitSaving(Rule(value: percent) with { Currency = currency }, price, currency));
    [Theory]
    [InlineData("Percentage", 0)]
    [InlineData("Percentage", -1)]
    [InlineData("Percentage", 101)]
    [InlineData("Fixed", 0)]
    [InlineData("Fixed", -1)]
    [InlineData("Unknown", 1)]
    public void Invalid_discount_values_are_rejected(string type, decimal value)
    {
        Assert.ThrowsAny<Exception>(() => DiscountRule.Create(new() { Name = "Offer", Currency = "IRR", Type = type, Value = value,
            StartsAtUtc = FixedClock.Now, EndsAtUtc = FixedClock.Now.AddDays(1) }, Guid.NewGuid(), FixedClock.Now));
        Assert.Throws<PricingException>(() => DiscountCalculation.UnitSaving(Rule(type, value), 1_000_000m, "IRR"));
    }
    [Fact]
    public void Discount_domain_validates_period_target_limits_precision_and_normalizes_coupon()
    {
        DiscountInput Input(DateTimeOffset? end = null, int? limit = null, Guid? variant = null, Guid? category = null, string? coupon = "sale") =>
            new() { Name = " Offer ", Currency = "irr", Value = 20, StartsAtUtc = FixedClock.Now, EndsAtUtc = end ?? FixedClock.Now.AddDays(1),
                UsageLimit = limit, ProductVariantId = variant, CategoryId = category, CouponCode = coupon };
        var actor = Guid.NewGuid(); var rule = DiscountRule.Create(Input(), actor, FixedClock.Now);
        Assert.Equal("SALE", rule.CouponCode); Assert.Equal("Offer", rule.Name); Assert.Equal("IRR", rule.Currency);
        Assert.Throws<DiscountRuleException>(() => DiscountRule.Create(Input(end: FixedClock.Now), actor, FixedClock.Now));
        Assert.Throws<DiscountRuleException>(() => DiscountRule.Create(Input(limit: 0), actor, FixedClock.Now));
        Assert.Throws<DiscountRuleException>(() => DiscountRule.Create(Input(variant: Guid.NewGuid(), category: Guid.NewGuid()), actor, FixedClock.Now));
        Assert.Throws<DiscountRuleException>(() => DiscountRule.Create(Input(coupon: "bad code"), actor, FixedClock.Now));
        Assert.Throws<DiscountRuleException>(() => rule.SetActive(false, Guid.Empty, FixedClock.Now));
        rule.SetActive(false, actor, FixedClock.Now); Assert.False(rule.IsActive);
    }
}
