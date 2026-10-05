using System.ComponentModel.DataAnnotations;

namespace MyOnlineShop.Pricing.Contracts;

public sealed class MoneyRuleException(string message) : Exception(message);
public static class MoneyRules
{
    public const decimal MaximumAmount = 1_000_000_000_000m;
    public static string Currency(string? currency)
    {
        var normalized = currency?.Trim().ToUpperInvariant();
        if (normalized is not ("IRR" or "USD" or "EUR" or "GBP" or "AED" or "TRY"))
            throw new MoneyRuleException("Unsupported currency. Specify IRR, USD, EUR, GBP, AED or TRY.");
        return normalized;
    }
    public static decimal Round(decimal amount, string currency) => decimal.Round(amount, Currency(currency) == "IRR" ? 0 : 2, MidpointRounding.AwayFromZero);
    public static void Amount(decimal amount, string currency, bool zeroAllowed = false)
    {
        if (amount < 0 || (!zeroAllowed && amount == 0) || amount > MaximumAmount || Round(amount, currency) != amount)
            throw new MoneyRuleException("Money amount is outside supported bounds or currency precision.");
    }
}
public sealed class PriceInput
{
    public Guid ProductVariantId { get; init; }
    public decimal BasePrice { get; init; }
    public decimal? ComparePrice { get; init; }
    [Required] public string Currency { get; init; } = "";
    public bool IsActive { get; init; } = true;
    public DateTimeOffset EffectiveFromUtc { get; init; }
    public DateTimeOffset? EffectiveToUtc { get; init; }
}
public sealed class PriceStatusInput { [Required] public bool? IsActive { get; init; } }
public sealed class PricingPreviewInput
{
    public Guid ProductVariantId { get; init; }
    [Range(1, 999)] public int Quantity { get; init; }
    [Required] public string Currency { get; init; } = "";
    [StringLength(64)] public string? CouponCode { get; init; }
}
public sealed record PriceDto(Guid Id, Guid ProductVariantId, decimal BasePrice, decimal? ComparePrice, string Currency,
    bool IsActive, DateTimeOffset EffectiveFromUtc, DateTimeOffset? EffectiveToUtc);
public sealed record PriceHistoryDto(Guid Id, Guid PriceId, string Action, PriceDto Snapshot, Guid ActorId, DateTimeOffset AtUtc, string CorrelationId);
public sealed record PriceLineRequest(Guid ProductVariantId, int Quantity);
public sealed record ApplicableDiscountDto(Guid Id, string Name, string Type, decimal Value, int Priority);
public sealed record PriceLineQuote(Guid ProductVariantId, int Quantity, Guid PriceId, string Currency, decimal BaseUnitPrice,
    decimal? CompareUnitPrice, Guid? DiscountId, decimal UnitDiscountAmount, decimal DiscountAmount, decimal FinalUnitPrice, decimal TotalLineAmount,
    IReadOnlyList<ApplicableDiscountDto> ApplicableDiscounts);
public sealed record PricingQuote(DateTimeOffset CalculatedAtUtc, string Currency, IReadOnlyList<PriceLineQuote> Lines);
public interface IPriceCommands
{
    Task<PriceDto> CreateAsync(PriceInput input, Guid actorId, CancellationToken ct);
    Task<PriceDto> UpdateAsync(Guid id, PriceInput input, Guid actorId, CancellationToken ct);
    Task<PriceDto> SetActiveAsync(Guid id, bool active, Guid actorId, CancellationToken ct);
}
public interface IPriceQueries
{
    Task<PriceDto?> GetCurrentAsync(Guid variantId, string currency, DateTimeOffset atUtc, CancellationToken ct);
    Task<IReadOnlyList<PriceDto>> GetCurrentManyAsync(IReadOnlyList<Guid> variantIds, string currency, DateTimeOffset atUtc, CancellationToken ct);
    Task<IReadOnlyList<PriceHistoryDto>> GetHistoryAsync(Guid variantId, string currency, int page, int pageSize, CancellationToken ct);
    Task<PriceDto> GetAsync(Guid id, CancellationToken ct);
}
public interface IPricingCalculation
{
    Task<PricingQuote> CalculateAsync(IReadOnlyList<PriceLineRequest> lines, string currency, string? couponCode, CancellationToken ct);
}
