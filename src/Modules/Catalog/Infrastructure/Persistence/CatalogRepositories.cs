using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Catalog.Application;
using MyOnlineShop.Catalog.Domain;

namespace MyOnlineShop.Catalog.Infrastructure.Persistence;

internal sealed class CategoryRepository(CatalogDbContext context) : ICategoryRepository
{
    public Task<Category?> GetAsync(Guid id, CancellationToken ct) => context.Categories.SingleOrDefaultAsync(value => value.Id == id, ct);
    public Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken ct) => context.Categories.AnyAsync(value => value.Code == code && value.Id != excludingId, ct);
    public Task<bool> HasActiveChildrenAsync(Guid id, CancellationToken ct) => context.Categories.AnyAsync(value => value.ParentId == id && value.IsActive, ct);
    public void Add(Category category) => context.Categories.Add(category);
}
internal sealed class BrandRepository(CatalogDbContext context) : IBrandRepository
{
    public Task<Brand?> GetAsync(Guid id, CancellationToken ct) => context.Brands.SingleOrDefaultAsync(value => value.Id == id, ct);
    public Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken ct) => context.Brands.AnyAsync(value => value.Code == code && value.Id != excludingId, ct);
    public void Add(Brand brand) => context.Brands.Add(brand);
}
internal sealed class AttributeRepository(CatalogDbContext context) : IAttributeRepository
{
    public Task<CatalogAttribute?> GetAsync(Guid id, CancellationToken ct) => context.Attributes.Include(value => value.Values).SingleOrDefaultAsync(value => value.Id == id, ct);
    public Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken ct) => context.Attributes.AnyAsync(value => value.Code == code && value.Id != excludingId, ct);
    public void Add(CatalogAttribute attribute) => context.Attributes.Add(attribute);
}
internal sealed class ProductRepository(CatalogDbContext context) : IProductRepository
{
    public Task<Product?> GetAsync(Guid id, CancellationToken ct) => context.Products
        .Include(value => value.AttributeOptions).Include(value => value.Images).Include(value => value.Specifications)
        .Include(value => value.Metadata).Include(value => value.Variants).ThenInclude(value => value.Values)
        .AsSplitQuery().SingleOrDefaultAsync(value => value.Id == id, ct);
    public Task<bool> SkuExistsAsync(string sku, Guid? excludingVariantId, CancellationToken ct) => context.Variants.AnyAsync(value => value.Sku == sku && value.Id != excludingVariantId, ct);
    public void Add(Product product) => context.Products.Add(product);
}
