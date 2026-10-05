using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Inventory.Domain;

namespace MyOnlineShop.Inventory.Infrastructure.Persistence;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Stock> Stocks => Set<Stock>();
    public DbSet<InventoryMovement> Movements => Set<InventoryMovement>();
    public DbSet<StockReceipt> Receipts => Set<StockReceipt>();
    public DbSet<StockAdjustment> Adjustments => Set<StockAdjustment>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("inventory");
        var warehouse = model.Entity<Warehouse>();
        warehouse.HasKey(value => value.Id); warehouse.Property(value => value.Id).ValueGeneratedNever();
        warehouse.Property(value => value.Name).HasMaxLength(100).IsRequired();
        warehouse.Property(value => value.Code).HasMaxLength(64).IsUnicode(false).IsRequired();
        warehouse.HasIndex(value => value.Code).IsUnique(); warehouse.Property(value => value.RowVersion).IsRowVersion();
        var stock = model.Entity<Stock>();
        stock.ToTable("Stocks", table =>
        {
            table.HasCheckConstraint("CK_Stocks_Quantity", "[Quantity] >= 0 AND [Quantity] <= 1000000000000");
            table.HasCheckConstraint("CK_Stocks_Threshold", "[LowStockThreshold] >= 0 AND [LowStockThreshold] <= 1000000000000");
        });
        stock.HasKey(value => value.Id); stock.Property(value => value.Id).ValueGeneratedNever();
        stock.HasIndex(value => new { value.WarehouseId, value.ProductVariantId }).IsUnique();
        stock.HasIndex(value => value.ProductVariantId);
        stock.Property(value => value.RowVersion).IsRowVersion();
        stock.HasOne<Warehouse>().WithMany().HasForeignKey(value => value.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        // ProductVariantId is an external module reference validated through Catalog's contract, not a cross-schema FK.
        var movement = model.Entity<InventoryMovement>();
        movement.ToTable("Movements", table =>
        {
            table.HasCheckConstraint("CK_Movements_Quantity", "[QuantityDelta] <> 0 AND [QuantityBefore] >= 0 AND [QuantityAfter] >= 0 AND [QuantityAfter] <= 1000000000000 AND [QuantityAfter] = [QuantityBefore] + [QuantityDelta]");
            table.HasCheckConstraint("CK_Movements_Type", "([Type] IN (1, 3) AND [QuantityDelta] > 0) OR ([Type] IN (2, 4) AND [QuantityDelta] < 0) OR [Type] = 5");
        });
        movement.HasKey(value => value.Id); movement.Property(value => value.Id).ValueGeneratedNever();
        movement.HasIndex(value => value.OperationId).IsUnique();
        movement.HasIndex(value => new { value.StockId, value.CreatedAtUtc });
        movement.HasIndex(value => new { value.Type, value.CreatedAtUtc });
        movement.HasIndex(value => value.Reference);
        movement.Property(value => value.Reference).HasMaxLength(200).IsRequired();
        movement.Property(value => value.Reason).HasMaxLength(500).IsRequired();
        movement.Property(value => value.CorrelationId).HasMaxLength(128).IsRequired();
        movement.Property(value => value.RequestHash).HasMaxLength(64).IsUnicode(false).IsFixedLength().IsRequired();
        movement.HasOne<Stock>().WithMany().HasForeignKey(value => value.StockId).OnDelete(DeleteBehavior.Restrict);
        var receipt = model.Entity<StockReceipt>();
        receipt.HasKey(value => value.Id); receipt.Property(value => value.Id).ValueGeneratedNever();
        receipt.HasOne<InventoryMovement>().WithOne().HasForeignKey<StockReceipt>(value => value.MovementId).OnDelete(DeleteBehavior.Restrict);
        var adjustment = model.Entity<StockAdjustment>();
        adjustment.HasKey(value => value.Id); adjustment.Property(value => value.Id).ValueGeneratedNever();
        adjustment.HasOne<InventoryMovement>().WithOne().HasForeignKey<StockAdjustment>(value => value.MovementId).OnDelete(DeleteBehavior.Restrict);
    }
    private void GuardHistory()
    {
        if (ChangeTracker.Entries().Any(entry => entry.Entity is InventoryMovement or StockReceipt or StockAdjustment &&
            entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Inventory history is append-only.");
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { GuardHistory(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { GuardHistory(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
}
