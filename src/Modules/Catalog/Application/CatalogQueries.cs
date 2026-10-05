using MyOnlineShop.Catalog.Contracts;

namespace MyOnlineShop.Catalog.Application;

public sealed class CatalogQueries(ICatalogReadStore store) : ICatalogQueries
{
    public Task<CategoryDto> GetCategoryAsync(Guid id, bool management, CancellationToken ct) => store.GetCategoryAsync(id, management, ct);
    public Task<BrandDto> GetBrandAsync(Guid id, bool management, CancellationToken ct) => store.GetBrandAsync(id, management, ct);
    public Task<AttributeDto> GetAttributeAsync(Guid id, bool management, CancellationToken ct) => store.GetAttributeAsync(id, management, ct);
    public Task<ProductDto> GetProductAsync(Guid id, bool management, CancellationToken ct) => store.GetProductAsync(id, management, ct);
    public Task<PageDto<CategoryDto>> ListCategoriesAsync(CatalogListQuery query, bool management, CancellationToken ct)
    { CatalogValidation.Validate(query); return store.ListCategoriesAsync(query, management, ct); }
    public Task<PageDto<BrandDto>> ListBrandsAsync(CatalogListQuery query, bool management, CancellationToken ct)
    { CatalogValidation.Validate(query); return store.ListBrandsAsync(query, management, ct); }
    public Task<PageDto<AttributeDto>> ListAttributesAsync(CatalogListQuery query, bool management, CancellationToken ct)
    { CatalogValidation.Validate(query); return store.ListAttributesAsync(query, management, ct); }
    public Task<PageDto<ProductSummaryDto>> ListProductsAsync(CatalogListQuery query, bool management, CancellationToken ct)
    { CatalogValidation.Validate(query); return store.ListProductsAsync(query, management, ct); }
}
