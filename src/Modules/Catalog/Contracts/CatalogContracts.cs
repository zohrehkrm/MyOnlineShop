using System.ComponentModel.DataAnnotations;

namespace MyOnlineShop.Catalog.Contracts;

public class NamedResourceInput
{
    [Required, StringLength(100)] public string Name { get; init; } = string.Empty;
    [Required, RegularExpression(@"^[A-Za-z0-9_.-]{1,64}$")] public string Code { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;
}
public sealed class CategoryInput : NamedResourceInput { public Guid? ParentId { get; init; } }
public sealed class BrandInput : NamedResourceInput;
public sealed class AttributeInput : NamedResourceInput;
public sealed class AttributeValueInput
{
    [Required, StringLength(200)] public string Label { get; init; } = string.Empty;
    [Required, RegularExpression(@"^[A-Za-z0-9_.-]{1,64}$")] public string Code { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;
}
public sealed class DimensionsInput
{
    [Range(typeof(decimal), "0.001", "1000000")] public decimal LengthMillimeters { get; init; }
    [Range(typeof(decimal), "0.001", "1000000")] public decimal WidthMillimeters { get; init; }
    [Range(typeof(decimal), "0.001", "1000000")] public decimal HeightMillimeters { get; init; }
}
public sealed class ProductAttributeInput
{
    public Guid AttributeId { get; init; }
    [Required, MinLength(1), MaxLength(64)] public List<Guid> ValueIds { get; init; } = [];
}
public sealed class AttributeSelectionInput
{
    public Guid AttributeId { get; init; }
    public Guid ValueId { get; init; }
}
public sealed class ImageInput
{
    [Required, StringLength(2048)] public string Url { get; init; } = string.Empty;
    [StringLength(200)] public string? AltText { get; init; }
    [Range(0, 10000)] public int SortOrder { get; init; }
    public bool IsPrimary { get; init; }
}
public sealed class SpecificationInput
{
    [Required, StringLength(100)] public string Name { get; init; } = string.Empty;
    [Required, StringLength(1000)] public string Value { get; init; } = string.Empty;
    [StringLength(32)] public string? Unit { get; init; }
    [Range(0, 10000)] public int SortOrder { get; init; }
}
public sealed class ProductInput
{
    [Required, StringLength(200)] public string Name { get; init; } = string.Empty;
    [StringLength(10000)] public string? Description { get; init; }
    public Guid CategoryId { get; init; }
    public Guid? BrandId { get; init; }
    [Required, RegularExpression("^(Physical|Digital)$")] public string Kind { get; init; } = "Physical";
    [Required, RegularExpression("^(Draft|Active|Archived)$")] public string Status { get; init; } = "Draft";
    [Range(typeof(decimal), "0.001", "1000000000")] public decimal? WeightGrams { get; init; }
    public DimensionsInput? Dimensions { get; init; }
    [Required, MaxLength(16)] public List<ProductAttributeInput> Attributes { get; init; } = [];
    [Required, MaxLength(30)] public List<ImageInput> Images { get; init; } = [];
    [Required, MaxLength(100)] public List<SpecificationInput> Specifications { get; init; } = [];
    [Required, MaxLength(50)] public Dictionary<string, string> Metadata { get; init; } = [];
}
public sealed class VariantInput
{
    [Required, RegularExpression(@"^[A-Za-z0-9_.-]{1,64}$")] public string Sku { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;
    [Range(typeof(decimal), "0.001", "1000000000")] public decimal? WeightGrams { get; init; }
    public DimensionsInput? Dimensions { get; init; }
    [Required, MaxLength(16)] public List<AttributeSelectionInput> Attributes { get; init; } = [];
}
public sealed class CatalogListQuery
{
    [Range(1, int.MaxValue)] public int Page { get; init; } = 1;
    [Range(1, 100)] public int PageSize { get; init; } = 20;
    [StringLength(200)] public string? Search { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? BrandId { get; init; }
    public Guid? ParentId { get; init; }
    public bool? IsActive { get; init; }
    [RegularExpression("^(Draft|Active|Archived)$")] public string? Status { get; init; }
    [RegularExpression("^(name|newest)$")] public string Sort { get; init; } = "name";
}

public sealed record PageDto<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
public sealed record CategoryDto(Guid Id, string Name, string Code, Guid? ParentId, bool IsActive);
public sealed record BrandDto(Guid Id, string Name, string Code, bool IsActive);
public sealed record AttributeValueDto(Guid Id, string Label, string Code, bool IsActive);
public sealed record AttributeDto(Guid Id, string Name, string Code, bool IsActive, IReadOnlyList<AttributeValueDto> Values);
public sealed record DimensionsDto(decimal LengthMillimeters, decimal WidthMillimeters, decimal HeightMillimeters);
public sealed record AttributeOptionDto(Guid AttributeId, Guid ValueId);
public sealed record ImageDto(Guid Id, string Url, string? AltText, int SortOrder, bool IsPrimary);
public sealed record SpecificationDto(Guid Id, string Name, string Value, string? Unit, int SortOrder);
public sealed record VariantDto(Guid Id, Guid ProductId, string Sku, bool IsActive, decimal? WeightGrams,
    DimensionsDto? Dimensions, IReadOnlyList<AttributeOptionDto> Attributes);
public sealed record ProductDto(Guid Id, string Name, string? Description, Guid CategoryId, Guid? BrandId, string Kind,
    string Status, decimal? WeightGrams, DimensionsDto? Dimensions, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<AttributeOptionDto> AttributeOptions, IReadOnlyList<ImageDto> Images,
    IReadOnlyList<SpecificationDto> Specifications, IReadOnlyDictionary<string, string> Metadata, IReadOnlyList<VariantDto> Variants);
public sealed record ProductSummaryDto(Guid Id, string Name, Guid CategoryId, Guid? BrandId, string Kind, string Status, string? PrimaryImageUrl);

public interface ICatalogCommands
{
    Task<CategoryDto> CreateCategoryAsync(CategoryInput input, CancellationToken ct);
    Task<CategoryDto> UpdateCategoryAsync(Guid id, CategoryInput input, CancellationToken ct);
    Task<BrandDto> CreateBrandAsync(BrandInput input, CancellationToken ct);
    Task<BrandDto> UpdateBrandAsync(Guid id, BrandInput input, CancellationToken ct);
    Task<AttributeDto> CreateAttributeAsync(AttributeInput input, CancellationToken ct);
    Task<AttributeDto> UpdateAttributeAsync(Guid id, AttributeInput input, CancellationToken ct);
    Task<AttributeValueDto> AddAttributeValueAsync(Guid attributeId, AttributeValueInput input, CancellationToken ct);
    Task<AttributeValueDto> UpdateAttributeValueAsync(Guid attributeId, Guid valueId, AttributeValueInput input, CancellationToken ct);
    Task<ProductDto> CreateProductAsync(ProductInput input, CancellationToken ct);
    Task<ProductDto> UpdateProductAsync(Guid id, ProductInput input, CancellationToken ct);
    Task<VariantDto> AddVariantAsync(Guid productId, VariantInput input, CancellationToken ct);
    Task<VariantDto> UpdateVariantAsync(Guid productId, Guid variantId, VariantInput input, CancellationToken ct);
}
public interface ICatalogQueries
{
    Task<CategoryDto> GetCategoryAsync(Guid id, bool management, CancellationToken ct);
    Task<BrandDto> GetBrandAsync(Guid id, bool management, CancellationToken ct);
    Task<AttributeDto> GetAttributeAsync(Guid id, bool management, CancellationToken ct);
    Task<ProductDto> GetProductAsync(Guid id, bool management, CancellationToken ct);
    Task<PageDto<CategoryDto>> ListCategoriesAsync(CatalogListQuery query, bool management, CancellationToken ct);
    Task<PageDto<BrandDto>> ListBrandsAsync(CatalogListQuery query, bool management, CancellationToken ct);
    Task<PageDto<AttributeDto>> ListAttributesAsync(CatalogListQuery query, bool management, CancellationToken ct);
    Task<PageDto<ProductSummaryDto>> ListProductsAsync(CatalogListQuery query, bool management, CancellationToken ct);
}
