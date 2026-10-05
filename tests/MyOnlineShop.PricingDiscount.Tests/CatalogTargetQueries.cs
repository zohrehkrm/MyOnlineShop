using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Application;

namespace MyOnlineShop.PricingDiscount.Tests;

internal sealed class CatalogTargetQueries(CatalogReferences references) : ICatalogQueries
{
    public Task<CategoryDto> GetCategoryAsync(Guid id, bool management, CancellationToken ct) =>
        id == references.CategoryId ? Task.FromResult(new CategoryDto(id, "Category", "CATEGORY", null, true)) : throw CatalogException.NotFound();
    public Task<ProductDto> GetProductAsync(Guid id, bool management, CancellationToken ct) =>
        id == references.ProductId ? Task.FromResult(new ProductDto(id, "Product", null, references.CategoryId, null, "Physical", "Active", null, null,
            FixedClock.Now, FixedClock.Now, [], [], [], new Dictionary<string, string>(), [])) : throw CatalogException.NotFound();
    public Task<BrandDto> GetBrandAsync(Guid id, bool management, CancellationToken ct) => throw new NotSupportedException();
    public Task<AttributeDto> GetAttributeAsync(Guid id, bool management, CancellationToken ct) => throw new NotSupportedException();
    public Task<PageDto<CategoryDto>> ListCategoriesAsync(CatalogListQuery query, bool management, CancellationToken ct) => throw new NotSupportedException();
    public Task<PageDto<BrandDto>> ListBrandsAsync(CatalogListQuery query, bool management, CancellationToken ct) => throw new NotSupportedException();
    public Task<PageDto<AttributeDto>> ListAttributesAsync(CatalogListQuery query, bool management, CancellationToken ct) => throw new NotSupportedException();
    public Task<PageDto<ProductSummaryDto>> ListProductsAsync(CatalogListQuery query, bool management, CancellationToken ct) => throw new NotSupportedException();
}
