using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Shipping.Contracts;
using MyOnlineShop.Shipping.Domain;

namespace MyOnlineShop.Shipping.Infrastructure.Persistence;

public sealed class ShippingDbContext(DbContextOptions<ShippingDbContext> options) : DbContext(options)
{
    public DbSet<ShippingMethod> Methods => Set<ShippingMethod>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShippingAudit> Audit => Set<ShippingAudit>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("shipping");
        var method = model.Entity<ShippingMethod>();
        method.ToTable("Methods", table =>
        {
            table.HasCheckConstraint("CK_ShippingMethods_Cost", "[BaseCost] >= 0 AND [BaseCost] <= 1000000000000");
            table.HasCheckConstraint("CK_ShippingMethods_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
        });
        method.HasKey(value => value.Id); method.Property(value => value.Id).ValueGeneratedNever(); method.HasIndex(value => value.Code).IsUnique();
        method.Property(value => value.Name).HasMaxLength(100).IsRequired(); method.Property(value => value.Code).HasMaxLength(64).IsUnicode(false).IsRequired();
        method.Property(value => value.Description).HasMaxLength(500); method.Property(value => value.BaseCost).HasPrecision(18, 4);
        method.Property(value => value.Currency).HasMaxLength(3).IsUnicode(false).IsRequired(); method.Property(value => value.RowVersion).IsRowVersion();
        var shipment = model.Entity<Shipment>();
        shipment.ToTable("Shipments", table =>
        {
            table.HasCheckConstraint("CK_Shipments_Cost", "[ShippingCost] >= 0 AND [ShippingCost] <= 1000000000000");
            table.HasCheckConstraint("CK_Shipments_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
            table.HasCheckConstraint("CK_Shipments_Status", "[Status] IN ('Pending','Preparing','Shipped','InTransit','Delivered','Cancelled')");
            table.HasCheckConstraint("CK_Shipments_Timestamps", "([Status] IN ('Pending','Preparing','Cancelled') AND [ShippedAtUtc] IS NULL AND [DeliveredAtUtc] IS NULL) OR ([Status] IN ('Shipped','InTransit') AND [ShippedAtUtc] IS NOT NULL AND [ShippedAtUtc] >= [CreatedAtUtc] AND [DeliveredAtUtc] IS NULL) OR ([Status] = 'Delivered' AND [ShippedAtUtc] IS NOT NULL AND [DeliveredAtUtc] IS NOT NULL AND [ShippedAtUtc] >= [CreatedAtUtc] AND [DeliveredAtUtc] >= [ShippedAtUtc])");
            table.HasCheckConstraint("CK_Shipments_Tracking", "[Status] NOT IN ('Shipped','InTransit','Delivered') OR [RequiresTracking] = 0 OR ([TrackingNumber] IS NOT NULL AND [Carrier] IS NOT NULL)");
        });
        shipment.HasKey(value => value.Id); shipment.Property(value => value.Id).ValueGeneratedNever(); shipment.HasIndex(value => value.OrderId).IsUnique();
        shipment.HasIndex(value => new { value.UserId, value.CreatedAtUtc }); shipment.HasIndex(value => new { value.Status, value.CreatedAtUtc });
        shipment.HasOne<ShippingMethod>().WithMany().HasForeignKey(value => value.ShippingMethodId).OnDelete(DeleteBehavior.Restrict);
        shipment.Property(value => value.MethodName).HasMaxLength(100).IsRequired(); shipment.Property(value => value.MethodCode).HasMaxLength(64).IsRequired();
        shipment.Property(value => value.ShippingCost).HasPrecision(18, 4); shipment.Property(value => value.Currency).HasMaxLength(3).IsUnicode(false).IsRequired();
        shipment.Property(value => value.Status).HasConversion<string>().HasMaxLength(16).IsRequired(); shipment.Property(value => value.RowVersion).IsRowVersion();
        shipment.Property(value => value.TrackingNumber).HasMaxLength(100); shipment.Property(value => value.Carrier).HasMaxLength(100);
        shipment.OwnsOne(value => value.Address, address =>
        {
            address.Property(value => value.Recipient).HasMaxLength(200).IsRequired(); address.Property(value => value.PhoneNumber).HasMaxLength(16).IsRequired();
            address.Property(value => value.State).HasMaxLength(100).IsRequired(); address.Property(value => value.City).HasMaxLength(100).IsRequired();
            address.Property(value => value.Street).HasMaxLength(500).IsRequired(); address.Property(value => value.PostalCode).HasMaxLength(20).IsRequired();
            address.Property(value => value.CountryCode).HasMaxLength(2).IsUnicode(false).IsRequired(); address.Property(value => value.Building).HasMaxLength(100);
            address.Property(value => value.Unit).HasMaxLength(30);
        });
        shipment.Navigation(value => value.Address).IsRequired();
        var audit = model.Entity<ShippingAudit>(); audit.HasKey(value => value.Id); audit.Property(value => value.Id).ValueGeneratedNever();
        audit.Property(value => value.Action).HasMaxLength(64).IsRequired(); audit.Property(value => value.CorrelationId).HasMaxLength(128).IsRequired();
        audit.HasIndex(value => new { value.EntityId, value.AtUtc });
    }
    private void GuardHistory()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Deleted || entry.Entity is ShippingAudit or ShippingAddressDto && entry.State == EntityState.Modified)
                throw new InvalidOperationException("Shipping snapshots/audit cannot be modified or deleted.");
            if (entry.Entity is Shipment && entry.State == EntityState.Modified &&
                entry.Properties.Any(property => property.IsModified && property.Metadata.Name is not
                    ("Status" or "TrackingNumber" or "Carrier" or "ShippedAtUtc" or "DeliveredAtUtc" or "Revision")))
                throw new InvalidOperationException("Shipment purchase data is immutable.");
        }
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess) { GuardHistory(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    { GuardHistory(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct); }
}
