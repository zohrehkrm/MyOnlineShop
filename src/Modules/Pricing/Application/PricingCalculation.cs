using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Discount.Contracts;
using MyOnlineShop.Pricing.Contracts;

namespace MyOnlineShop.Pricing.Application;

public static class DiscountCalculation
{
    public static bool Eligible(DiscountRuleDto rule, CatalogVariantReference variant, decimal subtotal, string currency, DateTimeOffset at, string? coupon) =>
        rule.IsActive && rule.Currency == currency && rule.StartsAtUtc <= at && at < rule.EndsAtUtc &&
        (rule.UsageLimit is null || rule.UsedCount < rule.UsageLimit) && rule.MinimumOrderAmount <= subtotal &&
        (rule.CouponCode is null || rule.CouponCode == coupon) &&
        (rule.ProductVariantId is null || rule.ProductVariantId == variant.Id) &&
        (rule.ProductId is null || rule.ProductId == variant.ProductId) &&
        (rule.CategoryId is null || rule.CategoryId == variant.CategoryId);
    public static decimal UnitSaving(DiscountRuleDto rule, decimal unitPrice, string currency)
    {
        if (rule.Value <= 0 || (rule.Type == "Percentage" && rule.Value > 100) || rule.Type is not ("Percentage" or "Fixed"))
            throw PricingException.Invalid("Invalid discount rule encountered.");
        var saving = rule.Type == "Percentage" ? unitPrice * rule.Value / 100m : rule.Value;
        return Math.Min(unitPrice, MoneyRules.Round(saving, currency));
    }
}
public sealed class PricingCalculation(IPriceQueries prices, IDiscountCandidates discounts, ICatalogVariantReferences catalog,
    TimeProvider clock) : IPricingCalculation
{
    public async Task<PricingQuote> CalculateAsync(IReadOnlyList<PriceLineRequest> lines, string currency, string? couponCode, CancellationToken ct)
    {
        currency = PricingValidation.Currency(currency);
        if (lines.Count > 100 || lines.Any(line => line is null || line.ProductVariantId == Guid.Empty || line.Quantity is < 1 or > 999) ||
            lines.Select(line => line.ProductVariantId).Distinct().Count() != lines.Count) throw PricingException.Invalid();
        var coupon = couponCode?.Trim().ToUpperInvariant();
        if (coupon is not null && (coupon.Length is < 1 or > 64 || coupon.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')))
            throw PricingException.Invalid("Coupon code is invalid.");
        var at = clock.GetUtcNow();
        if (lines.Count == 0) return new(at, currency, []);
        var ids = lines.Select(line => line.ProductVariantId).ToArray();
        var variants = (await catalog.GetManyAsync(ids, ct)).ToDictionary(value => value.Id);
        if (variants.Count != lines.Count || variants.Values.Any(value => !value.IsActive)) throw PricingException.Invalid("Variant is missing or not purchasable.");
        var current = (await prices.GetCurrentManyAsync(ids, currency, at, ct)).ToDictionary(value => value.ProductVariantId);
        if (current.Count != lines.Count) throw PricingException.NotFound();
        var subtotal = lines.Sum(line => current[line.ProductVariantId].BasePrice * line.Quantity);
        var rules = await discounts.GetAsync(currency, at, coupon, ct);
        var quotes = new List<PriceLineQuote>();
        foreach (var line in lines)
        {
            var price = current[line.ProductVariantId]; var variant = variants[line.ProductVariantId];
            var eligible = rules.Where(rule => DiscountCalculation.Eligible(rule, variant, subtotal, currency, at, coupon)).ToArray();
            var selected = eligible
                .Select(rule => new { Rule = rule, Saving = DiscountCalculation.UnitSaving(rule, price.BasePrice, currency) })
                .OrderByDescending(value => value.Rule.Priority).ThenByDescending(value => value.Saving).ThenBy(value => value.Rule.Id).FirstOrDefault();
            var saving = selected?.Saving ?? 0m;
            var final = price.BasePrice - saving;
            quotes.Add(new(line.ProductVariantId, line.Quantity, price.Id, currency, price.BasePrice, price.ComparePrice,
                selected?.Rule.Id, saving, saving * line.Quantity, final, final * line.Quantity,
                eligible.OrderByDescending(rule => rule.Priority).ThenBy(rule => rule.Id)
                    .Select(rule => new ApplicableDiscountDto(rule.Id, rule.Name, rule.Type, rule.Value, rule.Priority)).ToArray()));
        }
        return new(at, currency, quotes);
    }
}
