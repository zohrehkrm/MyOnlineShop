using MyOnlineShop.Catalog.Contracts;

namespace MyOnlineShop.Catalog.Application;

public sealed class CatalogCommands(CategoryCommands categories, TaxonomyCommands taxonomy, ProductCommands products) : ICatalogCommands
{
    public Task<CategoryDto> CreateCategoryAsync(CategoryInput input, CancellationToken ct) => categories.SaveAsync(null, input, ct);
    public Task<CategoryDto> UpdateCategoryAsync(Guid id, CategoryInput input, CancellationToken ct) => categories.SaveAsync(id, input, ct);
    public Task<BrandDto> CreateBrandAsync(BrandInput input, CancellationToken ct) => taxonomy.SaveBrandAsync(null, input, ct);
    public Task<BrandDto> UpdateBrandAsync(Guid id, BrandInput input, CancellationToken ct) => taxonomy.SaveBrandAsync(id, input, ct);
    public Task<AttributeDto> CreateAttributeAsync(AttributeInput input, CancellationToken ct) => taxonomy.SaveAttributeAsync(null, input, ct);
    public Task<AttributeDto> UpdateAttributeAsync(Guid id, AttributeInput input, CancellationToken ct) => taxonomy.SaveAttributeAsync(id, input, ct);
    public Task<AttributeValueDto> AddAttributeValueAsync(Guid id, AttributeValueInput input, CancellationToken ct) => taxonomy.SaveValueAsync(id, null, input, ct);
    public Task<AttributeValueDto> UpdateAttributeValueAsync(Guid id, Guid valueId, AttributeValueInput input, CancellationToken ct) => taxonomy.SaveValueAsync(id, valueId, input, ct);
    public Task<ProductDto> CreateProductAsync(ProductInput input, CancellationToken ct) => products.SaveAsync(null, input, ct);
    public Task<ProductDto> UpdateProductAsync(Guid id, ProductInput input, CancellationToken ct) => products.SaveAsync(id, input, ct);
    public Task<VariantDto> AddVariantAsync(Guid id, VariantInput input, CancellationToken ct) => products.SaveVariantAsync(id, null, input, ct);
    public Task<VariantDto> UpdateVariantAsync(Guid id, Guid variantId, VariantInput input, CancellationToken ct) => products.SaveVariantAsync(id, variantId, input, ct);
}
