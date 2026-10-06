using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Wallet.Domain;

namespace MyOnlineShop.Wallet.Infrastructure.Persistence;

public sealed class WalletDbContext(DbContextOptions<WalletDbContext> options) : DbContext(options)
{
    private readonly Dictionary<Guid, WalletBalanceChange> _changes = [];
    public DbSet<WalletAccount> Wallets => Set<WalletAccount>();
    public DbSet<WalletLedger> Ledger => Set<WalletLedger>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("wallet");
        var wallet = model.Entity<WalletAccount>();
        wallet.ToTable("Wallets", table =>
        {
            table.HasCheckConstraint("CK_Wallets_Balance", "[Balance] >= 0 AND [Balance] <= 1000000000000");
            table.HasCheckConstraint("CK_Wallets_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
        });
        wallet.HasKey(value => value.Id); wallet.Property(value => value.Id).ValueGeneratedNever();
        wallet.HasIndex(value => value.UserId).IsUnique();
        wallet.Property(value => value.Currency).HasMaxLength(3).IsUnicode(false).IsRequired();
        wallet.Property(value => value.Balance).HasPrecision(18, 4);
        wallet.Property(value => value.RowVersion).IsRowVersion();
        var ledger = model.Entity<WalletLedger>();
        ledger.ToTable("Ledger", table =>
        {
            table.HasTrigger("TR_WalletLedger_Immutable"); table.UseSqlOutputClause(false);
            table.HasCheckConstraint("CK_WalletLedger_Amount", "(([Type] = 'Credit' AND [Amount] > 0) OR ([Type] = 'Debit' AND [Amount] < 0)) AND ABS([Amount]) <= 1000000000000");
            table.HasCheckConstraint("CK_WalletLedger_Balances", "[BalanceBefore] >= 0 AND [BalanceBefore] <= 1000000000000 AND [BalanceAfter] >= 0 AND [BalanceAfter] <= 1000000000000 AND [BalanceAfter] = [BalanceBefore] + [Amount]");
            table.HasCheckConstraint("CK_WalletLedger_Status", "[Status] = 'Posted'");
            table.HasCheckConstraint("CK_WalletLedger_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
        });
        ledger.HasKey(value => value.Id); ledger.Property(value => value.Id).ValueGeneratedNever();
        ledger.HasOne<WalletAccount>().WithMany().HasForeignKey(value => value.WalletId).OnDelete(DeleteBehavior.Restrict);
        ledger.HasIndex(value => new { value.WalletId, value.IdempotencyKey }).IsUnique();
        ledger.HasIndex(value => new { value.WalletId, value.Type, value.ReferenceType, value.ReferenceId }).IsUnique();
        ledger.HasIndex(value => new { value.WalletId, value.CreatedAtUtc });
        ledger.Property(value => value.Amount).HasPrecision(18, 4); ledger.Property(value => value.BalanceBefore).HasPrecision(18, 4); ledger.Property(value => value.BalanceAfter).HasPrecision(18, 4);
        ledger.Property(value => value.Currency).HasMaxLength(3).IsUnicode(false).IsRequired();
        ledger.Property(value => value.Type).HasConversion<string>().HasMaxLength(16).IsRequired();
        ledger.Property(value => value.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        ledger.Property(value => value.ReferenceType).HasMaxLength(64).IsUnicode(false).IsRequired();
        ledger.Property(value => value.ReferenceId).HasMaxLength(128).IsRequired();
        ledger.Property(value => value.RequestFingerprint).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        ledger.Property(value => value.Description).HasMaxLength(500).IsRequired();
        ledger.Property(value => value.CorrelationId).HasMaxLength(128).IsRequired();
    }
    public void ResetPosting() { ChangeTracker.Clear(); _changes.Clear(); }
    public void RecordBalanceChange(WalletBalanceChange change)
    {
        if (!_changes.TryAdd(change.WalletId, change)) throw new InvalidOperationException("Only one posting per wallet is allowed in this operation.");
    }
    private void GuardFinancialHistory()
    {
        var entries = ChangeTracker.Entries<WalletLedger>().ToArray();
        if (entries.Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Wallet ledger history is immutable.");
        foreach (var entry in ChangeTracker.Entries<WalletAccount>())
        {
            var pairedUpdate = entry.State == EntityState.Modified && _changes.TryGetValue(entry.Entity.Id, out var change) &&
                entry.Entity.Balance == change.After && entry.Property(value => value.Balance).OriginalValue == change.Before &&
                entry.Properties.All(property => !property.IsModified || property.Metadata.Name is "Balance" or "UpdatedAtUtc");
            if (entry.State == EntityState.Deleted || entry.State == EntityState.Modified && !pairedUpdate ||
                entry.State == EntityState.Added && entry.Entity.Balance != 0m)
                throw new InvalidOperationException("Wallet mutations require an atomic posting; wallets cannot be deleted.");
        }
        var added = entries.Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity).ToArray();
        if (added.Length != _changes.Count || added.Select(entry => entry.WalletId).Distinct().Count() != added.Length || added.Any(entry => !_changes.TryGetValue(entry.WalletId, out var change) ||
            entry.BalanceBefore != change.Before || entry.BalanceAfter != change.After || entry.Amount != change.After - change.Before))
            throw new InvalidOperationException("Every balance change must have exactly one matching ledger entry.");
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardFinancialHistory(); var count = base.SaveChanges(acceptAllChangesOnSuccess); _changes.Clear(); return count;
    }
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        GuardFinancialHistory(); var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct); _changes.Clear(); return count;
    }
}
