using MyOnlineShop.Pricing.Contracts;

namespace MyOnlineShop.Pricing.Domain;

public sealed class PriceRuleException(string message) : Exception(message);
public sealed class VariantPrice
{
    private VariantPrice() { }
    public Guid Id { get; private set; }
    public Guid ProductVariantId { get; private set; }
    public decimal BasePrice { get; private set; }
    public decimal? ComparePrice { get; private set; }
    public string Currency { get; private set; } = "";
    public bool IsActive { get; private set; }
    public DateTimeOffset EffectiveFromUtc { get; private set; }
    public DateTimeOffset? EffectiveToUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public static VariantPrice Create(PriceInput input)
    {
        if (input.ProductVariantId == Guid.Empty) throw new PriceRuleException("Product variant is required.");
        var price = new VariantPrice { Id = Guid.NewGuid(), ProductVariantId = input.ProductVariantId, Currency = MoneyRules.Currency(input.Currency) };
        price.Update(input); return price;
    }
    public void Update(PriceInput input)
    {
        if (input.ProductVariantId != ProductVariantId || MoneyRules.Currency(input.Currency) != Currency)
            throw new PriceRuleException("A price's variant and currency cannot change. Create a separate price record.");
        MoneyRules.Amount(input.BasePrice, Currency);
        if (input.ComparePrice is { } compare)
        { MoneyRules.Amount(compare, Currency); if (compare < input.BasePrice) throw new PriceRuleException("Compare price cannot be below base price."); }
        if (input.EffectiveFromUtc == default || input.EffectiveToUtc <= input.EffectiveFromUtc)
            throw new PriceRuleException("Price effective period is invalid.");
        BasePrice = input.BasePrice; ComparePrice = input.ComparePrice; IsActive = input.IsActive;
        EffectiveFromUtc = input.EffectiveFromUtc.ToUniversalTime(); EffectiveToUtc = input.EffectiveToUtc?.ToUniversalTime();
    }
    public void SetActive(bool active) => IsActive = active;
    public PriceDto Dto() => new(Id, ProductVariantId, BasePrice, ComparePrice, Currency, IsActive, EffectiveFromUtc, EffectiveToUtc);
}
public sealed class PriceHistory
{
    private PriceHistory() { }
    public Guid Id { get; private set; }
    public Guid PriceId { get; private set; }
    public Guid ProductVariantId { get; private set; }
    public decimal BasePrice { get; private set; }
    public decimal? ComparePrice { get; private set; }
    public string Currency { get; private set; } = "";
    public bool IsActive { get; private set; }
    public DateTimeOffset EffectiveFromUtc { get; private set; }
    public DateTimeOffset? EffectiveToUtc { get; private set; }
    public string Action { get; private set; } = "";
    public Guid ActorId { get; private set; }
    public DateTimeOffset AtUtc { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public static PriceHistory Record(VariantPrice price, string action, Guid actorId, DateTimeOffset now, string correlation)
    {
        if (actorId == Guid.Empty) throw new PriceRuleException("Authenticated actor is required.");
        return new PriceHistory { Id = Guid.NewGuid(), PriceId = price.Id, ProductVariantId = price.ProductVariantId,
            BasePrice = price.BasePrice, ComparePrice = price.ComparePrice, Currency = price.Currency, IsActive = price.IsActive,
            EffectiveFromUtc = price.EffectiveFromUtc, EffectiveToUtc = price.EffectiveToUtc, Action = action, ActorId = actorId,
            AtUtc = now.ToUniversalTime(), CorrelationId = correlation };
    }
}
