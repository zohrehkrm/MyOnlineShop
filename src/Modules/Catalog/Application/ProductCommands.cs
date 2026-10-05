using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Domain;

namespace MyOnlineShop.Catalog.Application;

public sealed class ProductCommands(IProductRepository products, ICategoryRepository categories,
    IBrandRepository brands, IAttributeRepository attributes, ICatalogUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<ProductDto> SaveAsync(Guid? id, ProductInput input, CancellationToken ct)
    {
        CatalogValidation.Validate(input);
        return await unitOfWork.ExecuteAsync(async token =>
        {
            var category = await categories.GetAsync(input.CategoryId, token)
                ?? throw CatalogException.Invalid("Product category does not exist.");
            var brand = input.BrandId is null ? null : await brands.GetAsync(input.BrandId.Value, token)
                ?? throw CatalogException.Invalid("Product brand does not exist.");
            var options = CatalogValidation.Options(input);
            await ValidateOptionsAsync(options, input.Status != "Archived", token);
            if (input.Status == "Active" && (!category.IsActive || brand?.IsActive == false))
                throw CatalogException.Invalid("Active products require active category and brand references.");
            var existing = id is null ? null : await products.GetAsync(id.Value, token) ?? throw CatalogException.NotFound();
            var now = clock.GetUtcNow();
            var kind = Enum.Parse<ProductKind>(input.Kind);
            var product = existing ?? Product.Create(input.Name, input.Description, input.CategoryId, input.BrandId, kind, now);
            product.ConfigureAttributes(options);
            product.ReplaceDetails(input.Images.Select(image => new ImageDetails(image.Url, image.AltText, image.SortOrder, image.IsPrimary)).ToArray(),
                input.Specifications.Select(item => new SpecificationDetails(item.Name, item.Value, item.Unit, item.SortOrder)).ToArray(), input.Metadata);
            product.Update(input.Name, input.Description, input.CategoryId, input.BrandId, kind, Enum.Parse<ProductStatus>(input.Status),
                input.WeightGrams, CatalogValidation.Dimensions(input.Dimensions), now);
            if (existing is null) products.Add(product);
            return CatalogMapping.Dto(product);
        }, ct);
    }
    public async Task<VariantDto> SaveVariantAsync(Guid productId, Guid? variantId, VariantInput input, CancellationToken ct)
    {
        CatalogValidation.Validate(input);
        return await unitOfWork.ExecuteAsync(async token =>
        {
            var product = await products.GetAsync(productId, token) ?? throw CatalogException.NotFound();
            if (variantId is not null && !product.Variants.Any(variant => variant.Id == variantId)) throw CatalogException.NotFound();
            if (await products.SkuExistsAsync(CatalogRules.Code(input.Sku), variantId, token)) throw CatalogException.Conflict();
            var values = CatalogValidation.Selections(input);
            await ValidateOptionsAsync(values, input.IsActive, token);
            var dimensions = CatalogValidation.Dimensions(input.Dimensions);
            if (variantId is null)
                return CatalogMapping.Dto(product.AddVariant(input.Sku, input.IsActive, input.WeightGrams, dimensions, values, clock.GetUtcNow()));
            product.UpdateVariant(variantId.Value, input.Sku, input.IsActive, input.WeightGrams, dimensions, values, clock.GetUtcNow());
            return CatalogMapping.Dto(product.Variants.Single(variant => variant.Id == variantId));
        }, ct);
    }
    private async Task ValidateOptionsAsync(IReadOnlyList<AttributeOption> options, bool requireActive, CancellationToken ct)
    {
        foreach (var group in options.GroupBy(option => option.AttributeId))
        {
            var attribute = await attributes.GetAsync(group.Key, ct);
            if (attribute is null || (requireActive && !attribute.IsActive) ||
                group.Any(option => !attribute.Values.Any(value => value.Id == option.ValueId && (!requireActive || value.IsActive))))
                throw CatalogException.Invalid("Attribute values must belong to an active attribute and be active.");
        }
    }
}
