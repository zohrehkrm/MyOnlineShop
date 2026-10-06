using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Refund.Domain;
using RefundAggregate = MyOnlineShop.Refund.Domain.Refund;

namespace MyOnlineShop.Refund.Infrastructure.Persistence;

public sealed class RefundDbContext(DbContextOptions<RefundDbContext> options) : DbContext(options)
{
    public DbSet<RefundAggregate> Refunds => Set<RefundAggregate>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("refund");
        var entity = model.Entity<RefundAggregate>();
        entity.ToTable("Refunds", table =>
        {
            table.HasCheckConstraint("CK_Refund_Amount", "[Amount] > 0 AND [Amount] <= 1000000000000");
            table.HasCheckConstraint("CK_Refund_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
            table.HasCheckConstraint("CK_Refund_State", "[Status] IN ('Pending','Processing','Completed','Failed') AND [AttemptCount] >= 0");
            table.HasCheckConstraint("CK_Refund_Schedule", "[DueAtUtc] >= [CreatedAtUtc] AND [NextAttemptAtUtc] >= [DueAtUtc]");
            table.HasCheckConstraint("CK_Refund_Completion", "([Status] = 'Completed' AND [ProcessedAtUtc] IS NOT NULL AND [WalletTransactionId] IS NOT NULL) OR ([Status] <> 'Completed' AND [ProcessedAtUtc] IS NULL AND [WalletTransactionId] IS NULL)");
        });
        entity.HasKey(value => value.Id); entity.Property(value => value.Id).ValueGeneratedNever();
        entity.HasIndex(value => value.PaymentId).IsUnique(); entity.HasIndex(value => value.IdempotencyKey).IsUnique();
        entity.HasIndex(value => new { value.Status, value.NextAttemptAtUtc }); entity.HasIndex(value => value.OrderId);
        entity.Property(value => value.Amount).HasPrecision(18, 4);
        entity.Property(value => value.Currency).HasMaxLength(3).IsUnicode(false).IsRequired();
        entity.Property(value => value.Reason).HasMaxLength(64).IsRequired();
        entity.Property(value => value.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(value => value.FailureCode).HasMaxLength(64);
        entity.Property(value => value.CorrelationId).HasMaxLength(128).IsRequired();
        entity.Property(value => value.RowVersion).IsRowVersion();
        // Cross-module identifiers deliberately have no cross-schema entity relationships.
    }
    private void GuardHistory()
    {
        foreach (var entry in ChangeTracker.Entries<RefundAggregate>())
        {
            if (entry.State == EntityState.Deleted) throw new InvalidOperationException("Refund history cannot be deleted.");
            if (entry.State != EntityState.Modified) continue;
            if (entry.Property(value => value.Status).OriginalValue == RefundStatus.Completed ||
                entry.Properties.Any(property => property.IsModified && property.Metadata.Name is not
                    ("Status" or "NextAttemptAtUtc" or "ProcessedAtUtc" or "WalletTransactionId" or "AttemptCount" or "FailureCode")))
                throw new InvalidOperationException("Refund identity, value and completed history are immutable.");
        }
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { GuardHistory(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    { GuardHistory(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct); }
}
