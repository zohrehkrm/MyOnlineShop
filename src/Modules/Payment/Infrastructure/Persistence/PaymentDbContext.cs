using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Payment.Domain;
namespace MyOnlineShop.Payment.Infrastructure.Persistence;
public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options) {
 public DbSet<PaymentRecord> Payments => Set<PaymentRecord>();
 protected override void OnModelCreating(ModelBuilder model) {
  model.HasDefaultSchema("payment"); var p = model.Entity<PaymentRecord>();
  p.ToTable("Payments",table => {
   table.HasCheckConstraint("CK_Payment_Amount","[Amount] > 0 AND [Amount] <= 1000000000000");
   table.HasCheckConstraint("CK_Payment_Status","[Status] IN ('Created','Initiated','Succeeded','Failed')");
   table.HasCheckConstraint("CK_Payment_Fulfillment","[Fulfillment] IN ('Pending','Fulfilled','RefundPending','Refunded')");
   table.HasCheckConstraint("CK_Payment_Verified","([Status] = 'Succeeded' AND [VerifiedAtUtc] IS NOT NULL AND [ReceiptId] IS NOT NULL) OR ([Status] <> 'Succeeded' AND [VerifiedAtUtc] IS NULL AND [ReceiptId] IS NULL)");
   table.HasCheckConstraint("CK_Payment_Currency","[Currency] IN ('IRR','USD','EUR','GBP','AED','TRY')");
  });
  p.HasKey(x => x.Id); p.Property(x => x.Id).ValueGeneratedNever();
  p.HasIndex(x => x.OrderId).IsUnique(); p.HasIndex(x => new {x.UserId,x.IdempotencyKey}).IsUnique();
  p.HasIndex(x => new {x.Provider,x.ProviderReference}).IsUnique().HasFilter("[ProviderReference] IS NOT NULL");
  p.HasIndex(x => new {x.Provider,x.ReceiptId}).IsUnique().HasFilter("[ReceiptId] IS NOT NULL");
  p.Property(x => x.Amount).HasPrecision(18,4); p.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
  p.Property(x => x.Provider).HasMaxLength(64); p.Property(x => x.ProviderReference).HasMaxLength(128); p.Property(x => x.ReceiptId).HasMaxLength(128);
  p.Property(x => x.CheckoutUrl).HasMaxLength(2048); p.Property(x => x.CorrelationId).HasMaxLength(128);
  p.Property(x => x.Status).HasConversion<string>().HasMaxLength(32); p.Property(x => x.Fulfillment).HasConversion<string>().HasMaxLength(32);
  p.Property(x => x.RowVersion).IsRowVersion();
  p.Property(x => x.ReconciliationReason).HasConversion<string>().HasMaxLength(64);
  p.HasMany(x => x.Allocations).WithOne().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
  p.Navigation(x => x.Allocations).UsePropertyAccessMode(PropertyAccessMode.Field);
  var line = model.Entity<PaymentAllocation>(); line.ToTable("Allocations",table => table.HasCheckConstraint("CK_PaymentAllocation_Quantity","[Quantity] BETWEEN 1 AND 999"));
  line.HasKey(x => new {x.PaymentId,x.ProductVariantId}); line.Property(x => x.PaymentId).ValueGeneratedNever();
 }
}

