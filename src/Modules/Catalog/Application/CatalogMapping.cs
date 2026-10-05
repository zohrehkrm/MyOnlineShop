using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Domain;

namespace MyOnlineShop.Catalog.Application;

public static class CatalogMapping
{
    public static CategoryDto Dto(Category value) => new(value.Id, value.Name, value.Code, value.ParentId, value.IsActive);
    public static BrandDto Dto(Brand value) => new(value.Id, value.Name, value.Code, value.IsActive);
    public static AttributeValueDto Dto(AttributeValue value) => new(value.Id, value.Label, value.Code, value.IsActive);
    public static AttributeDto Dto(CatalogAttribute value, bool management = true) => new(value.Id, value.Name, value.Code, value.IsActive,
        value.Values.Where(item => management || item.IsActive).OrderBy(item => item.Label).Select(Dto).ToArray());
    public static DimensionsDto? Dto(Dimensions? value) => value is null ? null : new(value.LengthMillimeters, value.WidthMillimeters, value.HeightMillimeters);
    public static VariantDto Dto(ProductVariant value) => new(value.Id, value.ProductId, value.Sku, value.IsActive, value.WeightGrams, Dto(value.Dimensions),
        value.Values.OrderBy(item => item.AttributeId).Select(item => new AttributeOptionDto(item.AttributeId, item.ValueId)).ToArray());
    public static ProductDto Dto(Product value, bool management = true) => new(value.Id, value.Name, value.Description,
        value.CategoryId, value.BrandId, value.Kind.ToString(), value.Status.ToString(), value.WeightGrams, Dto(value.Dimensions),
        value.CreatedAtUtc, value.UpdatedAtUtc, value.AttributeOptions.Select(item => new AttributeOptionDto(item.AttributeId, item.ValueId)).ToArray(),
        value.Images.OrderBy(item => item.SortOrder).Select(item => new ImageDto(item.Id, item.Url, item.AltText, item.SortOrder, item.IsPrimary)).ToArray(),
        value.Specifications.OrderBy(item => item.SortOrder).ThenBy(item => item.Name).Select(item => new SpecificationDto(item.Id, item.Name, item.Value, item.Unit, item.SortOrder)).ToArray(),
        value.Metadata.ToDictionary(item => item.Key, item => item.Value), value.Variants.Where(item => management || item.IsActive).OrderBy(item => item.Sku).Select(Dto).ToArray());
}
