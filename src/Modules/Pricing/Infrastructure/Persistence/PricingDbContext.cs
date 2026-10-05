using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Pricing.Domain;

namespace MyOnlineShop.Pricing.Infrastructure.Persistence;

public sealed class PricingDbContext(DbContextOptions<PricingDbContext> options) : DbContext(options)
{
    public DbSet<VariantPrice> Prices => Set<VariantPrice>();
    public DbSet<PriceHistory> History => Set<PriceHistory>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("pricing");
        var price = model.Entity<VariantPrice>();
        price.ToTable("Prices", table =>
        {
            table.HasCheckConstraint("CK_Prices_Amounts", "[BasePrice] > 0 AND [BasePrice] <= 1000000000000 AND ([ComparePrice] IS NULL OR ([ComparePrice] >= [BasePrice] AND [ComparePrice] <= 1000000000000))");
            table.HasCheckConstraint("CK_Prices_Period", "[EffectiveToUtc] IS NULL OR [EffectiveToUtc] > [EffectiveFromUtc]");
            table.HasCheckConstraint("CK_Prices_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
        });
        price.HasKey(value => value.Id); price.Property(value => value.Id).ValueGeneratedNever();
        price.Property(value => value.BasePrice).HasPrecision(18, 4); price.Property(value => value.ComparePrice).HasPrecision(18, 4);
        price.Property(value => value.Currency).HasMaxLength(3).IsUnicode(false).IsRequired();
        price.Property(value => value.RowVersion).IsRowVersion();
        price.HasIndex(value => new { value.ProductVariantId, value.Currency, value.EffectiveFromUtc }).IsUnique().HasFilter("[IsActive] = 1");
        price.HasIndex(value => new { value.ProductVariantId, value.Currency, value.IsActive, value.EffectiveFromUtc, value.EffectiveToUtc });
        var history = model.Entity<PriceHistory>();
        history.HasKey(value => value.Id); history.Property(value => value.Id).ValueGeneratedNever();
        history.HasOne<VariantPrice>().WithMany().HasForeignKey(value => value.PriceId).OnDelete(DeleteBehavior.Restrict);
        history.Property(value => value.BasePrice).HasPrecision(18, 4); history.Property(value => value.ComparePrice).HasPrecision(18, 4);
        history.Property(value => value.Currency).HasMaxLength(3).IsUnicode(false).IsRequired();
        history.Property(value => value.Action).HasMaxLength(32).IsRequired(); history.Property(value => value.CorrelationId).HasMaxLength(128).IsRequired();
        history.HasIndex(value => new { value.ProductVariantId, value.Currency, value.AtUtc });
    }
    private void GuardHistory()
    {
        if (ChangeTracker.Entries<PriceHistory>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Price history is append-only.");
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess) { GuardHistory(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    { GuardHistory(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct); }
}
