using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Discount.Domain;

namespace MyOnlineShop.Discount.Infrastructure.Persistence;

public sealed class DiscountDbContext(DbContextOptions<DiscountDbContext> options) : DbContext(options)
{
    public DbSet<DiscountRule> Rules => Set<DiscountRule>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("discount"); var rule = model.Entity<DiscountRule>();
        rule.ToTable("Rules", table =>
        {
            table.HasCheckConstraint("CK_Rules_Value", "([Type] = 'Percentage' AND [Value] > 0 AND [Value] <= 100) OR ([Type] = 'Fixed' AND [Value] > 0 AND [Value] <= 1000000000000)");
            table.HasCheckConstraint("CK_Rules_Period", "[EndsAtUtc] > [StartsAtUtc]");
            table.HasCheckConstraint("CK_Rules_Limits", "[Priority] >= 0 AND [Priority] <= 10000 AND [MinimumOrderAmount] >= 0 AND [MinimumOrderAmount] <= 1000000000000 AND [UsedCount] >= 0 AND ([UsageLimit] IS NULL OR ([UsageLimit] > 0 AND [UsedCount] <= [UsageLimit]))");
            table.HasCheckConstraint("CK_Rules_Target", "(CASE WHEN [ProductVariantId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [ProductId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [CategoryId] IS NULL THEN 0 ELSE 1 END) <= 1");
            table.HasCheckConstraint("CK_Rules_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
        });
        rule.HasKey(value => value.Id); rule.Property(value => value.Id).ValueGeneratedNever();
        rule.Property(value => value.Name).HasMaxLength(100).IsRequired(); rule.Property(value => value.Type).HasMaxLength(16).IsRequired();
        rule.Property(value => value.Value).HasPrecision(18, 4); rule.Property(value => value.MinimumOrderAmount).HasPrecision(18, 4);
        rule.Property(value => value.Currency).HasMaxLength(3).IsUnicode(false).IsRequired();
        rule.Property(value => value.CouponCode).HasMaxLength(64).IsUnicode(false);
        rule.Property(value => value.RowVersion).IsRowVersion();
        rule.HasIndex(value => new { value.Currency, value.IsActive, value.StartsAtUtc, value.EndsAtUtc });
        rule.HasIndex(value => new { value.Currency, value.CouponCode });
        rule.HasIndex(value => value.ProductVariantId); rule.HasIndex(value => value.ProductId); rule.HasIndex(value => value.CategoryId);
    }
}
