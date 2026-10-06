using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Catalog.Application;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Domain;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Infrastructure.Caching;

namespace MyOnlineShop.Catalog.Infrastructure.Persistence;

internal sealed class CatalogReadStore(CatalogDbContext context, ReadCache? cache = null) : ICatalogReadStore
{
    public Task<CategoryDto> GetCategoryAsync(Guid id, bool management, CancellationToken ct) => cache is null
        ? CategoryAsync(id, management, ct) : cache.GetAsync("catalog", CacheKeys.Detail("category", id, management), token => CategoryAsync(id, management, token), ct);
    public Task<BrandDto> GetBrandAsync(Guid id, bool management, CancellationToken ct) => cache is null
        ? BrandAsync(id, management, ct) : cache.GetAsync("catalog", CacheKeys.Detail("brand", id, management), token => BrandAsync(id, management, token), ct);
    public Task<ProductDto> GetProductAsync(Guid id, bool management, CancellationToken ct) => cache is null
        ? ProductAsync(id, management, ct) : cache.GetAsync("catalog", CacheKeys.Detail("product", id, management), token => ProductAsync(id, management, token), ct);
    private IQueryable<Product> VisibleProducts(bool management)
    {
        var products = context.Products.AsNoTracking();
        if (management) return products;
        return products.Where(product => product.Status == ProductStatus.Active &&
            context.Categories.Any(category => category.Id == product.CategoryId && category.IsActive) &&
            (product.BrandId == null || context.Brands.Any(brand => brand.Id == product.BrandId && brand.IsActive)) &&
            product.Variants.Any(variant => variant.IsActive && variant.Values.All(selection =>
                context.AttributeValues.Any(value => value.Id == selection.ValueId && value.IsActive) &&
                context.Attributes.Any(attribute => attribute.Id == selection.AttributeId && attribute.IsActive))));
    }

    private async Task<CategoryDto> CategoryAsync(Guid id, bool management, CancellationToken ct) =>
        await context.Categories.AsNoTracking().Where(value => value.Id == id && (management || value.IsActive))
            .Select(value => new CategoryDto(value.Id, value.Name, value.Code, value.ParentId, value.IsActive)).SingleOrDefaultAsync(ct)
        ?? throw CatalogException.NotFound();
    private async Task<BrandDto> BrandAsync(Guid id, bool management, CancellationToken ct) =>
        await context.Brands.AsNoTracking().Where(value => value.Id == id && (management || value.IsActive))
            .Select(value => new BrandDto(value.Id, value.Name, value.Code, value.IsActive)).SingleOrDefaultAsync(ct)
        ?? throw CatalogException.NotFound();
    public async Task<AttributeDto> GetAttributeAsync(Guid id, bool management, CancellationToken ct) =>
        await context.Attributes.AsNoTracking().Where(value => value.Id == id && (management || value.IsActive))
            .Select(value => new AttributeDto(value.Id, value.Name, value.Code, value.IsActive,
                value.Values.Where(item => management || item.IsActive).OrderBy(item => item.Label)
                    .Select(item => new AttributeValueDto(item.Id, item.Label, item.Code, item.IsActive)).ToList()))
            .SingleOrDefaultAsync(ct) ?? throw CatalogException.NotFound();

    private async Task<ProductDto> ProductAsync(Guid id, bool management, CancellationToken ct)
    {
        var product = await VisibleProducts(management).Where(value => value.Id == id)
            .Include(value => value.AttributeOptions).Include(value => value.Images).Include(value => value.Specifications)
            .Include(value => value.Metadata).Include(value => value.Variants).ThenInclude(value => value.Values)
            .AsSplitQuery().SingleOrDefaultAsync(ct) ?? throw CatalogException.NotFound();
        var dto = CatalogMapping.Dto(product, management);
        if (management) return dto;
        var relevantIds = product.AttributeOptions.Select(option => option.ValueId).ToArray();
        var activeOptions = await context.AttributeValues.AsNoTracking()
            .Where(value => relevantIds.Contains(value.Id) && value.IsActive &&
                context.Attributes.Any(attribute => attribute.Id == value.AttributeId && attribute.IsActive))
            .Select(value => new AttributeOptionDto(value.AttributeId, value.Id)).ToListAsync(ct);
        var allowed = activeOptions.ToHashSet();
        return dto with
        {
            AttributeOptions = dto.AttributeOptions.Where(allowed.Contains).ToArray(),
            Variants = dto.Variants.Where(variant => variant.Attributes.All(allowed.Contains)).ToArray()
        };
    }
    public async Task<PageDto<CategoryDto>> ListCategoriesAsync(CatalogListQuery query, bool management, CancellationToken ct)
    {
        var values = context.Categories.AsNoTracking().Where(value => management || value.IsActive);
        if (query.IsActive is not null) values = values.Where(value => value.IsActive == query.IsActive);
        if (query.ParentId is not null) values = values.Where(value => value.ParentId == query.ParentId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            values = values.Where(value => value.Name.ToUpper().Contains(term) || value.Code.Contains(term));
        }
        var count = await values.CountAsync(ct);
        var items = await values.OrderBy(value => value.Name).ThenBy(value => value.Id).Skip(Offset(query)).Take(query.PageSize)
            .Select(value => new CategoryDto(value.Id, value.Name, value.Code, value.ParentId, value.IsActive)).ToListAsync(ct);
        return new(items, query.Page, query.PageSize, count);
    }
    public async Task<PageDto<BrandDto>> ListBrandsAsync(CatalogListQuery query, bool management, CancellationToken ct)
    {
        var values = context.Brands.AsNoTracking().Where(value => management || value.IsActive);
        if (query.IsActive is not null) values = values.Where(value => value.IsActive == query.IsActive);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            values = values.Where(value => value.Name.ToUpper().Contains(term) || value.Code.Contains(term));
        }
        var count = await values.CountAsync(ct);
        var items = await values.OrderBy(value => value.Name).ThenBy(value => value.Id).Skip(Offset(query)).Take(query.PageSize)
            .Select(value => new BrandDto(value.Id, value.Name, value.Code, value.IsActive)).ToListAsync(ct);
        return new(items, query.Page, query.PageSize, count);
    }
    public async Task<PageDto<AttributeDto>> ListAttributesAsync(CatalogListQuery query, bool management, CancellationToken ct)
    {
        var values = context.Attributes.AsNoTracking().Where(value => management || value.IsActive);
        if (query.IsActive is not null) values = values.Where(value => value.IsActive == query.IsActive);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            values = values.Where(value => value.Name.ToUpper().Contains(term) || value.Code.Contains(term));
        }
        var count = await values.CountAsync(ct);
        var items = await values.OrderBy(value => value.Name).ThenBy(value => value.Id).Skip(Offset(query)).Take(query.PageSize)
            .Select(value => new AttributeDto(value.Id, value.Name, value.Code, value.IsActive,
                value.Values.Where(item => management || item.IsActive).OrderBy(item => item.Label)
                    .Select(item => new AttributeValueDto(item.Id, item.Label, item.Code, item.IsActive)).ToList())).ToListAsync(ct);
        return new(items, query.Page, query.PageSize, count);
    }
    public async Task<PageDto<ProductSummaryDto>> ListProductsAsync(CatalogListQuery query, bool management, CancellationToken ct)
    {
        var values = VisibleProducts(management);
        if (query.CategoryId is not null) values = values.Where(value => value.CategoryId == query.CategoryId);
        if (query.BrandId is not null) values = values.Where(value => value.BrandId == query.BrandId);
        if (query.Status is not null)
        {
            var status = Enum.Parse<ProductStatus>(query.Status);
            values = values.Where(value => value.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            values = values.Where(value => value.Name.ToUpper().Contains(term) || (value.Description != null && value.Description.ToUpper().Contains(term)) ||
                value.Variants.Any(variant => variant.Sku.Contains(term)));
        }
        var count = await values.CountAsync(ct);
        var ordered = query.Sort == "newest" ? values.OrderByDescending(value => value.CreatedAtUtc).ThenBy(value => value.Id)
            : values.OrderBy(value => value.Name).ThenBy(value => value.Id);
        var items = await ordered.Skip(Offset(query)).Take(query.PageSize).Select(value =>
            new ProductSummaryDto(value.Id, value.Name, value.CategoryId, value.BrandId, value.Kind.ToString(), value.Status.ToString(),
                value.Images.Where(image => image.IsPrimary).Select(image => image.Url).FirstOrDefault())).ToListAsync(ct);
        return new(items, query.Page, query.PageSize, count);
    }
    private static int Offset(CatalogListQuery query) => checked((query.Page - 1) * query.PageSize);
}
