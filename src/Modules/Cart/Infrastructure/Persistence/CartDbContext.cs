using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Cart.Domain;
using CartAggregate = MyOnlineShop.Cart.Domain.Cart;

namespace MyOnlineShop.Cart.Infrastructure.Persistence;

public sealed class CartDbContext(DbContextOptions<CartDbContext> options) : DbContext(options)
{
    public DbSet<CartAggregate> Carts => Set<CartAggregate>();
    public DbSet<CartItem> Items => Set<CartItem>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("cart");
        var cart = model.Entity<CartAggregate>();
        cart.HasKey(value => value.Id); cart.Property(value => value.Id).ValueGeneratedNever();
        cart.HasIndex(value => value.UserId).IsUnique().HasFilter("[IsActive] = 1");
        cart.Property(value => value.RowVersion).IsRowVersion();
        cart.HasMany(value => value.Items).WithOne().HasForeignKey(value => value.CartId).OnDelete(DeleteBehavior.Cascade);
        cart.Navigation(value => value.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        var item = model.Entity<CartItem>();
        item.ToTable("Items", table => table.HasCheckConstraint("CK_Items_Quantity", "[Quantity] >= 1 AND [Quantity] <= 999"));
        item.HasKey(value => value.Id); item.Property(value => value.Id).ValueGeneratedNever();
        item.HasIndex(value => new { value.CartId, value.ProductVariantId }).IsUnique();
        // UserId and ProductVariantId are module references, not cross-schema foreign keys.
    }
}
