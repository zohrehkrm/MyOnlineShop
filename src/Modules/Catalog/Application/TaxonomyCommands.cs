using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Domain;

namespace MyOnlineShop.Catalog.Application;

public sealed class TaxonomyCommands(IBrandRepository brands, IAttributeRepository attributes, ICatalogUnitOfWork unitOfWork)
{
    public async Task<BrandDto> SaveBrandAsync(Guid? id, BrandInput input, CancellationToken ct)
    {
        CatalogValidation.Validate(input);
        return await unitOfWork.ExecuteAsync(async token =>
        {
            if (await brands.CodeExistsAsync(CatalogRules.Code(input.Code), id, token)) throw CatalogException.Conflict();
            var existing = id is null ? null : await brands.GetAsync(id.Value, token) ?? throw CatalogException.NotFound();
            var brand = existing ?? Brand.Create(input.Name, input.Code, input.IsActive);
            brand.Update(input.Name, input.Code, input.IsActive);
            if (existing is null) brands.Add(brand);
            return CatalogMapping.Dto(brand);
        }, ct);
    }
    public async Task<AttributeDto> SaveAttributeAsync(Guid? id, AttributeInput input, CancellationToken ct)
    {
        CatalogValidation.Validate(input);
        return await unitOfWork.ExecuteAsync(async token =>
        {
            if (await attributes.CodeExistsAsync(CatalogRules.Code(input.Code), id, token)) throw CatalogException.Conflict();
            var existing = id is null ? null : await attributes.GetAsync(id.Value, token) ?? throw CatalogException.NotFound();
            var attribute = existing ?? CatalogAttribute.Create(input.Name, input.Code, input.IsActive);
            attribute.Update(input.Name, input.Code, input.IsActive);
            if (existing is null) attributes.Add(attribute);
            return CatalogMapping.Dto(attribute);
        }, ct);
    }
    public async Task<AttributeValueDto> SaveValueAsync(Guid attributeId, Guid? valueId, AttributeValueInput input, CancellationToken ct)
    {
        CatalogValidation.Validate(input);
        return await unitOfWork.ExecuteAsync(async token =>
        {
            var attribute = await attributes.GetAsync(attributeId, token) ?? throw CatalogException.NotFound();
            if (valueId is null) return CatalogMapping.Dto(attribute.AddValue(input.Label, input.Code, input.IsActive));
            if (!attribute.Values.Any(value => value.Id == valueId)) throw CatalogException.NotFound();
            attribute.UpdateValue(valueId.Value, input.Label, input.Code, input.IsActive);
            return CatalogMapping.Dto(attribute.Values.Single(value => value.Id == valueId));
        }, ct);
    }
}
