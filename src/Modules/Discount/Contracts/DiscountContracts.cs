using System.ComponentModel.DataAnnotations;

namespace MyOnlineShop.Discount.Contracts;

public sealed class DiscountInput
{
    [Required, StringLength(100)] public string Name { get; init; } = "";
    [Required, RegularExpression("^(Percentage|Fixed)$")] public string Type { get; init; } = "Percentage";
    public decimal Value { get; init; }
    [Required] public string Currency { get; init; } = "";
    public bool IsActive { get; init; } = true;
    public DateTimeOffset StartsAtUtc { get; init; }
    public DateTimeOffset EndsAtUtc { get; init; }
    [Range(0, 10000)] public int Priority { get; init; }
    public decimal MinimumOrderAmount { get; init; }
    [Range(1, int.MaxValue)] public int? UsageLimit { get; init; }
    public Guid? ProductVariantId { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? CategoryId { get; init; }
    [StringLength(64), RegularExpression("^[A-Za-z0-9_-]{1,64}$")] public string? CouponCode { get; init; }
}
public sealed class DiscountStatusInput { [Required] public bool? IsActive { get; init; } }
public sealed record DiscountRuleDto(Guid Id, string Name, string Type, decimal Value, string Currency, bool IsActive,
    DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, int Priority, decimal MinimumOrderAmount, int? UsageLimit,
    int UsedCount, Guid? ProductVariantId, Guid? ProductId, Guid? CategoryId, string? CouponCode);
public interface IDiscountCommands
{
    Task<DiscountRuleDto> CreateAsync(DiscountInput input, Guid actorId, CancellationToken ct);
    Task<DiscountRuleDto> UpdateAsync(Guid id, DiscountInput input, Guid actorId, CancellationToken ct);
    Task<DiscountRuleDto> SetActiveAsync(Guid id, bool active, Guid actorId, CancellationToken ct);
}
public interface IDiscountQueries
{
    Task<DiscountRuleDto> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<DiscountRuleDto>> ListAsync(int page, int pageSize, CancellationToken ct);
}
// Candidate rules only. Pricing calculates server-owned subtotal/target eligibility and monetary amounts.
public interface IDiscountCandidates
{
    Task<IReadOnlyList<DiscountRuleDto>> GetAsync(string currency, DateTimeOffset atUtc, string? couponCode, CancellationToken ct);
}
