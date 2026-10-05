using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyOnlineShop.Catalog.Domain;

namespace MyOnlineShop.Catalog.Infrastructure.Persistence;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories", table => table.HasCheckConstraint("CK_Categories_Parent", "[ParentId] IS NULL OR [ParentId] <> [Id]"));
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.Name).HasMaxLength(100).IsRequired();
        builder.Property(value => value.Code).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.HasIndex(value => value.Code).IsUnique();
        builder.HasIndex(value => new { value.IsActive, value.Name });
        builder.Property(value => value.RowVersion).IsRowVersion();
        builder.HasOne<Category>().WithMany().HasForeignKey(value => value.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class BrandConfiguration : IEntityTypeConfiguration<Brand>
{
    public void Configure(EntityTypeBuilder<Brand> builder)
    {
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.Name).HasMaxLength(100).IsRequired();
        builder.Property(value => value.Code).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.HasIndex(value => value.Code).IsUnique();
        builder.HasIndex(value => new { value.IsActive, value.Name });
        builder.Property(value => value.RowVersion).IsRowVersion();
    }
}
public sealed class AttributeConfiguration : IEntityTypeConfiguration<CatalogAttribute>
{
    public void Configure(EntityTypeBuilder<CatalogAttribute> builder)
    {
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.Name).HasMaxLength(100).IsRequired();
        builder.Property(value => value.Code).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.HasIndex(value => value.Code).IsUnique();
        builder.Property(value => value.RowVersion).IsRowVersion();
        builder.HasMany(value => value.Values).WithOne().HasForeignKey(value => value.AttributeId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(value => value.Values).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
public sealed class AttributeValueConfiguration : IEntityTypeConfiguration<AttributeValue>
{
    public void Configure(EntityTypeBuilder<AttributeValue> builder)
    {
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.HasAlternateKey(value => new { value.AttributeId, value.Id });
        builder.Property(value => value.Label).HasMaxLength(200).IsRequired();
        builder.Property(value => value.Code).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.HasIndex(value => new { value.AttributeId, value.Code }).IsUnique();
    }
}
public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", table =>
        {
            table.HasCheckConstraint("CK_Products_Status", "[Status] IN (1, 2, 3)");
            table.HasCheckConstraint("CK_Products_Kind", "[Kind] IN (1, 2)");
            table.HasCheckConstraint("CK_Products_Weight", "[WeightGrams] IS NULL OR [WeightGrams] > 0");
        });
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.Name).HasMaxLength(200).IsRequired();
        builder.Property(value => value.Description).HasMaxLength(10000);
        builder.Property(value => value.WeightGrams).HasPrecision(18, 3);
        builder.Property(value => value.RowVersion).IsRowVersion();
        builder.HasIndex(value => new { value.Status, value.CategoryId, value.Name });
        builder.HasIndex(value => new { value.Status, value.BrandId });
        builder.HasIndex(value => value.CreatedAtUtc);
        builder.HasOne<Category>().WithMany().HasForeignKey(value => value.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Brand>().WithMany().HasForeignKey(value => value.BrandId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(value => value.Variants).WithOne().HasForeignKey(value => value.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(value => value.AttributeOptions).WithOne().HasForeignKey(value => value.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(value => value.Images).WithOne().HasForeignKey(value => value.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(value => value.Specifications).WithOne().HasForeignKey(value => value.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(value => value.Metadata).WithOne().HasForeignKey(value => value.ProductId).OnDelete(DeleteBehavior.Cascade);
        foreach (var navigation in new[] { nameof(Product.Variants), nameof(Product.AttributeOptions), nameof(Product.Images), nameof(Product.Specifications), nameof(Product.Metadata) })
            builder.Navigation(navigation).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.OwnsOne(value => value.Dimensions, dimensions =>
        {
            dimensions.Property(value => value.LengthMillimeters).HasColumnName("LengthMillimeters").HasPrecision(18, 3);
            dimensions.Property(value => value.WidthMillimeters).HasColumnName("WidthMillimeters").HasPrecision(18, 3);
            dimensions.Property(value => value.HeightMillimeters).HasColumnName("HeightMillimeters").HasPrecision(18, 3);
        });
    }
}
public sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.ToTable("Variants", table => table.HasCheckConstraint("CK_Variants_Weight", "[WeightGrams] IS NULL OR [WeightGrams] > 0"));
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.HasAlternateKey(value => new { value.ProductId, value.Id });
        builder.Property(value => value.Sku).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(value => value.CombinationHash).HasMaxLength(64).IsUnicode(false).IsFixedLength().IsRequired();
        builder.Property(value => value.WeightGrams).HasPrecision(18, 3);
        builder.HasIndex(value => value.Sku).IsUnique();
        builder.HasIndex(value => new { value.ProductId, value.CombinationHash }).IsUnique();
        builder.HasMany(value => value.Values).WithOne().HasForeignKey(value => new { value.ProductId, value.VariantId })
            .HasPrincipalKey(value => new { value.ProductId, value.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(value => value.Values).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.OwnsOne(value => value.Dimensions, dimensions =>
        {
            dimensions.Property(value => value.LengthMillimeters).HasColumnName("LengthMillimeters").HasPrecision(18, 3);
            dimensions.Property(value => value.WidthMillimeters).HasColumnName("WidthMillimeters").HasPrecision(18, 3);
            dimensions.Property(value => value.HeightMillimeters).HasColumnName("HeightMillimeters").HasPrecision(18, 3);
        });
    }
}
public sealed class ProductAttributeOptionConfiguration : IEntityTypeConfiguration<ProductAttributeOption>
{
    public void Configure(EntityTypeBuilder<ProductAttributeOption> builder)
    {
        builder.HasKey(value => new { value.ProductId, value.AttributeId, value.ValueId });
        builder.HasOne<AttributeValue>().WithMany().HasForeignKey(value => new { value.AttributeId, value.ValueId })
            .HasPrincipalKey(value => new { value.AttributeId, value.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class VariantValueConfiguration : IEntityTypeConfiguration<VariantAttributeValue>
{
    public void Configure(EntityTypeBuilder<VariantAttributeValue> builder)
    {
        builder.HasKey(value => new { value.VariantId, value.AttributeId });
        builder.HasOne<ProductAttributeOption>().WithMany().HasForeignKey(value => new { value.ProductId, value.AttributeId, value.ValueId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class ImageConfiguration : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> builder)
    {
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.Url).HasMaxLength(2048).IsRequired();
        builder.Property(value => value.AltText).HasMaxLength(200);
        builder.HasIndex(value => new { value.ProductId, value.SortOrder }).IsUnique();
        builder.HasIndex(value => value.ProductId).IsUnique().HasFilter("[IsPrimary] = 1");
    }
}
public sealed class SpecificationConfiguration : IEntityTypeConfiguration<ProductSpecification>
{
    public void Configure(EntityTypeBuilder<ProductSpecification> builder)
    {
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.Name).HasMaxLength(100).IsRequired();
        builder.Property(value => value.NormalizedName).HasMaxLength(100).IsRequired();
        builder.Property(value => value.Value).HasMaxLength(1000).IsRequired();
        builder.Property(value => value.Unit).HasMaxLength(32);
        builder.HasIndex(value => new { value.ProductId, value.NormalizedName }).IsUnique();
    }
}
public sealed class MetadataConfiguration : IEntityTypeConfiguration<ProductMetadata>
{
    public void Configure(EntityTypeBuilder<ProductMetadata> builder)
    {
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Id).ValueGeneratedNever();
        builder.Property(value => value.Key).HasMaxLength(100).IsRequired();
        builder.Property(value => value.NormalizedKey).HasMaxLength(100).IsRequired();
        builder.Property(value => value.Value).HasMaxLength(1000).IsRequired();
        builder.HasIndex(value => new { value.ProductId, value.NormalizedKey }).IsUnique();
    }
}
