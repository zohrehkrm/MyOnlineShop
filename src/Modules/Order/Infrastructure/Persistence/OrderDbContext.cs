using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Order.Domain;
using MyOnlineShop.Shipping.Contracts;
using OrderAggregate = MyOnlineShop.Order.Domain.Order;

namespace MyOnlineShop.Order.Infrastructure.Persistence;

public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<OrderAggregate> Orders => Set<OrderAggregate>();
    public DbSet<OrderItem> Items => Set<OrderItem>();
    public DbSet<OrderAudit> Audit => Set<OrderAudit>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("ordering");
        var order = model.Entity<OrderAggregate>();
        order.ToTable("Orders", table =>
        {
            table.HasCheckConstraint("CK_Orders_Totals", "[Subtotal] > 0 AND [Subtotal] <= 1000000000000 AND [DiscountTotal] >= 0 AND [DiscountTotal] <= [Subtotal] AND [ShippingCost] >= 0 AND [ShippingCost] <= 1000000000000 AND [PayableAmount] = [Subtotal] - [DiscountTotal] + [ShippingCost] AND [PayableAmount] <= 1000000000000");
            table.HasCheckConstraint("CK_Orders_Status", "[Status] IN ('Pending','AwaitingPayment','Paid','Processing','Shipped','Completed','Cancelled','Failed')");
            table.HasCheckConstraint("CK_Orders_Currency", "[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
        });
        order.HasKey(value => value.Id); order.Property(value => value.Id).ValueGeneratedNever();
        order.HasIndex(value => new { value.UserId, value.IdempotencyKey }).IsUnique();
        order.HasIndex(value => new { value.CartId, value.CartRevision }).IsUnique();
        order.HasIndex(value => new { value.UserId, value.CreatedAtUtc });
        order.HasIndex(value => new { value.Status, value.CreatedAtUtc });
        order.Property(value => value.RequestFingerprint).HasMaxLength(64).IsUnicode(false).IsFixedLength().IsRequired();
        order.Property(value => value.Currency).HasMaxLength(3).IsUnicode(false).IsRequired();
        order.Property(value => value.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        order.Property(value => value.RowVersion).IsRowVersion();
        order.Property(value => value.Subtotal).HasPrecision(18, 4);
        order.Property(value => value.DiscountTotal).HasPrecision(18, 4);
        order.Property(value => value.PayableAmount).HasPrecision(18, 4);
        order.Property(value => value.ShippingCost).HasPrecision(18, 4).HasDefaultValue(0m);
        order.OwnsOne(value => value.Shipping, shipping =>
        {
            shipping.Property(value => value.MethodCode).HasMaxLength(64).IsRequired();
            shipping.Property(value => value.MethodName).HasMaxLength(100).IsRequired();
            shipping.Property(value => value.Cost).HasPrecision(18, 4);
            shipping.Property(value => value.Currency).HasMaxLength(3).IsUnicode(false).IsRequired();
            shipping.OwnsOne(value => value.Address, address =>
            {
                address.Property(value => value.Recipient).HasMaxLength(200).IsRequired();
                address.Property(value => value.PhoneNumber).HasMaxLength(16).IsRequired();
                address.Property(value => value.State).HasMaxLength(100).IsRequired();
                address.Property(value => value.City).HasMaxLength(100).IsRequired();
                address.Property(value => value.Street).HasMaxLength(500).IsRequired();
                address.Property(value => value.PostalCode).HasMaxLength(20).IsRequired();
                address.Property(value => value.CountryCode).HasMaxLength(2).IsUnicode(false).IsRequired();
                address.Property(value => value.Building).HasMaxLength(100);
                address.Property(value => value.Unit).HasMaxLength(30);
            });
            shipping.Navigation(value => value.Address).IsRequired();
        });
        order.HasMany(value => value.Items).WithOne().HasForeignKey(value => value.OrderId).OnDelete(DeleteBehavior.Restrict);
        order.Navigation(value => value.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        order.OwnsOne(value => value.Address, address =>
        {
            address.Property(value => value.Recipient).HasMaxLength(200).IsRequired();
            address.Property(value => value.Street).HasMaxLength(500).IsRequired();
            address.Property(value => value.City).HasMaxLength(100).IsRequired();
            address.Property(value => value.PostalCode).HasMaxLength(20).IsRequired();
            address.Property(value => value.CountryCode).HasMaxLength(2).IsUnicode(false).IsRequired();
        });
        var item = model.Entity<OrderItem>();
        item.ToTable("Items", table =>
        {
            table.HasCheckConstraint("CK_OrderItems_Quantity", "[Quantity] >= 1 AND [Quantity] <= 999");
            table.HasCheckConstraint("CK_OrderItems_Amounts", "[UnitPrice] > 0 AND [UnitPrice] <= 1000000000000 AND [UnitDiscount] >= 0 AND [UnitDiscount] <= [UnitPrice] AND [FinalUnitPrice] = [UnitPrice] - [UnitDiscount] AND [DiscountAmount] = [UnitDiscount] * [Quantity] AND [LineTotal] = [FinalUnitPrice] * [Quantity]");
            table.HasCheckConstraint("CK_OrderItems_Kind", "[ProductKind] IN ('Physical','Digital')");
        });
        item.HasKey(value => value.Id); item.Property(value => value.Id).ValueGeneratedNever();
        item.HasIndex(value => new { value.OrderId, value.ProductVariantId }).IsUnique();
        item.Property(value => value.Sku).HasMaxLength(100).IsRequired();
        item.Property(value => value.ProductName).HasMaxLength(200).IsRequired();
        item.Property(value => value.ProductKind).HasMaxLength(16).IsRequired();
        foreach (var name in new[] { nameof(OrderItem.UnitPrice), nameof(OrderItem.UnitDiscount), nameof(OrderItem.DiscountAmount), nameof(OrderItem.FinalUnitPrice), nameof(OrderItem.LineTotal) })
            item.Property<decimal>(name).HasPrecision(18, 4);
        var audit = model.Entity<OrderAudit>();
        audit.HasKey(value => value.Id); audit.Property(value => value.Id).ValueGeneratedNever();
        audit.HasOne<OrderAggregate>().WithMany().HasForeignKey(value => value.OrderId).OnDelete(DeleteBehavior.Restrict);
        audit.Property(value => value.Action).HasMaxLength(32).IsRequired();
        audit.Property(value => value.CorrelationId).HasMaxLength(128).IsRequired();
        audit.HasIndex(value => new { value.OrderId, value.AtUtc });
    }
    private void GuardSnapshots()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is OrderItem or OrderAddress or OrderAudit or ShippingQuoteSnapshot or ShippingAddressDto && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Order snapshots and audit are immutable.");
            if (entry.Entity is OrderAggregate && (entry.State == EntityState.Deleted ||
                entry.Properties.Any(property => property.IsModified && property.Metadata.Name is not ("Status" or "UpdatedAtUtc" or "Revision"))))
                throw new InvalidOperationException("Order purchase data is immutable.");
        }
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess) { GuardSnapshots(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    { GuardSnapshots(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct); }
}
