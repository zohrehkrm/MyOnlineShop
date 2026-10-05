using MyOnlineShop.Discount.Contracts;
using MyOnlineShop.Pricing.Contracts;

namespace MyOnlineShop.Discount.Domain;

public sealed class DiscountRuleException(string message) : Exception(message);
public sealed class DiscountRule
{
    private DiscountRule() { }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string Type { get; private set; } = "";
    public decimal Value { get; private set; }
    public string Currency { get; private set; } = "";
    public bool IsActive { get; private set; }
    public DateTimeOffset StartsAtUtc { get; private set; }
    public DateTimeOffset EndsAtUtc { get; private set; }
    public int Priority { get; private set; }
    public decimal MinimumOrderAmount { get; private set; }
    public int? UsageLimit { get; private set; }
    public int UsedCount { get; private set; }
    public Guid? ProductVariantId { get; private set; }
    public Guid? ProductId { get; private set; }
    public Guid? CategoryId { get; private set; }
    public string? CouponCode { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public static DiscountRule Create(DiscountInput input, Guid actorId, DateTimeOffset now)
    { var value = new DiscountRule { Id = Guid.NewGuid() }; value.Update(input, actorId, now); return value; }
    public void Update(DiscountInput input, Guid actorId, DateTimeOffset now)
    {
        var currency = MoneyRules.Currency(input.Currency);
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 100 || actorId == Guid.Empty ||
            input.Type is not ("Percentage" or "Fixed") || input.Value <= 0 || input.StartsAtUtc == default ||
            input.EndsAtUtc <= input.StartsAtUtc || input.Priority is < 0 or > 10000 || input.UsageLimit is <= 0 ||
            input.UsageLimit < UsedCount || input.ProductVariantId == Guid.Empty || input.ProductId == Guid.Empty || input.CategoryId == Guid.Empty ||
            new[] { input.ProductVariantId, input.ProductId, input.CategoryId }.Count(id => id is not null) > 1)
            throw new DiscountRuleException("Discount data, target or effective period is invalid.");
        if (input.Type == "Percentage" && (input.Value > 100 || decimal.Round(input.Value, 4) != input.Value))
            throw new DiscountRuleException("Percentage must be greater than zero and at most 100, with at most four decimal places.");
        if (input.Type == "Fixed") MoneyRules.Amount(input.Value, currency);
        MoneyRules.Amount(input.MinimumOrderAmount, currency, true);
        var code = input.CouponCode?.Trim().ToUpperInvariant();
        if (code is not null && (code.Length is < 1 or > 64 || code.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')))
            throw new DiscountRuleException("Coupon code is invalid.");
        Name = input.Name.Trim(); Type = input.Type; Value = input.Value; Currency = currency; IsActive = input.IsActive;
        StartsAtUtc = input.StartsAtUtc.ToUniversalTime(); EndsAtUtc = input.EndsAtUtc.ToUniversalTime(); Priority = input.Priority;
        MinimumOrderAmount = input.MinimumOrderAmount; UsageLimit = input.UsageLimit;
        ProductVariantId = input.ProductVariantId; ProductId = input.ProductId; CategoryId = input.CategoryId; CouponCode = code;
        UpdatedBy = actorId; UpdatedAtUtc = now.ToUniversalTime();
    }
    public void SetActive(bool active, Guid actorId, DateTimeOffset now)
    {
        if (actorId == Guid.Empty) throw new DiscountRuleException("Authenticated actor is required.");
        IsActive = active; UpdatedBy = actorId; UpdatedAtUtc = now.ToUniversalTime();
    }
    public DiscountRuleDto Dto() => new(Id, Name, Type, Value, Currency, IsActive, StartsAtUtc, EndsAtUtc, Priority,
        MinimumOrderAmount, UsageLimit, UsedCount, ProductVariantId, ProductId, CategoryId, CouponCode);
}
