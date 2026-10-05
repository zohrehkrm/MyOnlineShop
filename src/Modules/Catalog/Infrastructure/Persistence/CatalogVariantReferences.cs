using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Domain;

namespace MyOnlineShop.Catalog.Infrastructure.Persistence;

internal sealed class CatalogVariantReferences(CatalogDbContext context) : ICatalogVariantReferences
{
    public Task<CatalogVariantReference?> GetAsync(Guid variantId, CancellationToken ct) =>
        (from variant in context.Variants.AsNoTracking()
         join product in context.Products.AsNoTracking() on variant.ProductId equals product.Id
         where variant.Id == variantId
         select new CatalogVariantReference(variant.Id, variant.Sku, product.Kind.ToString(),
             variant.IsActive && product.Status == ProductStatus.Active &&
             context.Categories.Any(category => category.Id == product.CategoryId && category.IsActive) &&
             (product.BrandId == null || context.Brands.Any(brand => brand.Id == product.BrandId && brand.IsActive)) &&
             variant.Values.All(selection => context.AttributeValues.Any(value => value.Id == selection.ValueId && value.IsActive) &&
                 context.Attributes.Any(attribute => attribute.Id == selection.AttributeId && attribute.IsActive))))
        .SingleOrDefaultAsync(ct);
}
