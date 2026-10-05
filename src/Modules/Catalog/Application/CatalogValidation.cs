using System.ComponentModel.DataAnnotations;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Domain;

namespace MyOnlineShop.Catalog.Application;

public static class CatalogValidation
{
    public static void Validate(object input)
    {
        if (!Validator.TryValidateObject(input, new ValidationContext(input), [], true))
            throw CatalogException.Invalid("Catalog input is invalid.");
        if (input is CatalogListQuery query && ((long)query.Page - 1) * query.PageSize > int.MaxValue)
            throw CatalogException.Invalid("Requested page is outside supported limits.");
        if (input is ProductInput product)
        {
            if (product.CategoryId == Guid.Empty || product.BrandId == Guid.Empty ||
                product.Attributes.Select(attribute => attribute?.AttributeId).Distinct().Count() != product.Attributes.Count)
                throw CatalogException.Invalid("Product category, brand or attribute definitions are invalid.");
            foreach (var attribute in product.Attributes)
            {
                if (attribute is null) throw CatalogException.Invalid("Product attribute definition is required.");
                Validate(attribute);
                if (attribute.AttributeId == Guid.Empty || attribute.ValueIds.Any(value => value == Guid.Empty) ||
                    attribute.ValueIds.Distinct().Count() != attribute.ValueIds.Count)
                    throw CatalogException.Invalid("Product attribute values are invalid or duplicated.");
            }
            foreach (var image in product.Images) { if (image is null) throw CatalogException.Invalid("Image is required."); Validate(image); }
            foreach (var specification in product.Specifications) { if (specification is null) throw CatalogException.Invalid("Specification is required."); Validate(specification); }
            if (product.Dimensions is not null) Validate(product.Dimensions);
        }
        if (input is VariantInput variant)
        {
            if (variant.Dimensions is not null) Validate(variant.Dimensions);
            if (variant.Attributes.Any(value => value is null || value.AttributeId == Guid.Empty || value.ValueId == Guid.Empty) ||
                variant.Attributes.Select(value => value.AttributeId).Distinct().Count() != variant.Attributes.Count)
                throw CatalogException.Invalid("Variant must contain distinct attributes with valid value IDs.");
        }
    }
    public static Dimensions? Dimensions(DimensionsInput? input) => input is null ? null :
        new(input.LengthMillimeters, input.WidthMillimeters, input.HeightMillimeters);
    public static IReadOnlyList<AttributeOption> Options(ProductInput input) => input.Attributes
        .SelectMany(attribute => attribute.ValueIds.Select(value => new AttributeOption(attribute.AttributeId, value))).ToArray();
    public static IReadOnlyList<AttributeOption> Selections(VariantInput input) => input.Attributes
        .Select(value => new AttributeOption(value.AttributeId, value.ValueId)).ToArray();
}
