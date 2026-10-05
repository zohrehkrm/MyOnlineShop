namespace MyOnlineShop.Catalog.Domain;

public sealed class Product
{
    private readonly List<ProductVariant> _variants = [];
    private readonly List<ProductAttributeOption> _attributeOptions = [];
    private readonly List<ProductImage> _images = [];
    private readonly List<ProductSpecification> _specifications = [];
    private readonly List<ProductMetadata> _metadata = [];
    private Product() { }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid CategoryId { get; private set; }
    public Guid? BrandId { get; private set; }
    public ProductStatus Status { get; private set; }
    public ProductKind Kind { get; private set; }
    public decimal? WeightGrams { get; private set; }
    public Dimensions? Dimensions { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public Guid Revision { get; private set; }
    public IReadOnlyList<ProductVariant> Variants => _variants.AsReadOnly();
    public IReadOnlyList<ProductAttributeOption> AttributeOptions => _attributeOptions.AsReadOnly();
    public IReadOnlyList<ProductImage> Images => _images.AsReadOnly();
    public IReadOnlyList<ProductSpecification> Specifications => _specifications.AsReadOnly();
    public IReadOnlyList<ProductMetadata> Metadata => _metadata.AsReadOnly();

    public static Product Create(string name, string? description, Guid categoryId, Guid? brandId, ProductKind kind, DateTimeOffset now)
    {
        var product = new Product { Id = Guid.NewGuid(), CreatedAtUtc = now };
        product.Update(name, description, categoryId, brandId, kind, ProductStatus.Draft, null, null, now);
        return product;
    }
    public void Update(string name, string? description, Guid categoryId, Guid? brandId, ProductKind kind,
        ProductStatus status, decimal? weight, Dimensions? dimensions, DateTimeOffset now)
    {
        var validName = CatalogRules.Required(name, 200, "Product name");
        CatalogRules.Id(categoryId, "Category");
        if (brandId == Guid.Empty || description?.Length > 10000 || !Enum.IsDefined(kind) || !Enum.IsDefined(status))
            throw new CatalogRuleException("Product references, description, kind or status are invalid.");
        ValidatePhysicalData(kind, weight, dimensions);
        if (kind == ProductKind.Digital && _variants.Any(variant => variant.WeightGrams is not null || variant.Dimensions is not null))
            throw new CatalogRuleException("Digital products cannot retain physical variant measurements.");
        if (status == ProductStatus.Active && !_variants.Any(variant => variant.IsActive))
            throw new CatalogRuleException("An active product requires at least one active variant.");
        Name = validName; Description = description; CategoryId = categoryId; BrandId = brandId;
        Kind = kind; Status = status; WeightGrams = weight; Dimensions = dimensions; UpdatedAtUtc = now;
        Revision = Guid.NewGuid();
    }
    public void ConfigureAttributes(IReadOnlyList<AttributeOption> options)
    {
        if (options.Count > 256 || options.Select(option => option.AttributeId).Distinct().Count() > 16 ||
            options.Any(option => option.AttributeId == Guid.Empty || option.ValueId == Guid.Empty) || options.Distinct().Count() != options.Count)
            throw new CatalogRuleException("Product attribute options are invalid or duplicated.");
        var oldAttributes = _attributeOptions.Select(option => option.AttributeId).ToHashSet();
        var newAttributes = options.Select(option => option.AttributeId).ToHashSet();
        if (_variants.Count > 0 && !oldAttributes.SetEquals(newAttributes))
            throw new CatalogRuleException("Variant-defining attributes cannot be changed while variants exist.");
        if (_variants.SelectMany(variant => variant.Values).Any(value => !options.Contains(new(value.AttributeId, value.ValueId))))
            throw new CatalogRuleException("An option used by an existing variant cannot be removed.");
        _attributeOptions.RemoveAll(existing => !options.Contains(new(existing.AttributeId, existing.ValueId)));
        foreach (var option in options)
            if (!_attributeOptions.Any(existing => existing.AttributeId == option.AttributeId && existing.ValueId == option.ValueId))
                _attributeOptions.Add(new(Id, option.AttributeId, option.ValueId));
        Revision = Guid.NewGuid();
    }
    public ProductVariant AddVariant(string sku, bool active, decimal? weight, Dimensions? dimensions, IReadOnlyList<AttributeOption> values, DateTimeOffset now)
    {
        ValidateVariant(null, sku, active, weight, dimensions, values);
        if (_variants.Count >= 1000) throw new CatalogRuleException("Product variant limit reached.");
        var variant = ProductVariant.Create(Id, sku, active, weight, dimensions, values);
        _variants.Add(variant); UpdatedAtUtc = now; Revision = Guid.NewGuid(); return variant;
    }
    public void UpdateVariant(Guid variantId, string sku, bool active, decimal? weight, Dimensions? dimensions, IReadOnlyList<AttributeOption> values, DateTimeOffset now)
    {
        var variant = _variants.SingleOrDefault(variant => variant.Id == variantId)
            ?? throw new CatalogRuleException("Variant does not belong to this product.");
        ValidateVariant(variantId, sku, active, weight, dimensions, values);
        variant.Update(sku, active, weight, dimensions, values); UpdatedAtUtc = now; Revision = Guid.NewGuid();
    }
    private void ValidateVariant(Guid? variantId, string sku, bool active, decimal? weight, Dimensions? dimensions, IReadOnlyList<AttributeOption> values)
    {
        ValidatePhysicalData(Kind, weight, dimensions);
        var expected = _attributeOptions.Select(option => option.AttributeId).Distinct().ToHashSet();
        if (values.Count != expected.Count || !expected.SetEquals(values.Select(value => value.AttributeId)) ||
            values.Any(value => !_attributeOptions.Any(option => option.AttributeId == value.AttributeId && option.ValueId == value.ValueId)))
            throw new CatalogRuleException("Each variant must select exactly one allowed value for every variant-defining attribute.");
        var normalizedSku = CatalogRules.Code(sku);
        var combination = ProductVariant.HashCombination(values);
        if (_variants.Any(other => other.Id != variantId && (other.Sku == normalizedSku || other.CombinationHash == combination)))
            throw new CatalogRuleException("Variant SKU or attribute combination already exists for this product.");
        if (!active && Status == ProductStatus.Active && !_variants.Any(other => other.Id != variantId && other.IsActive))
            throw new CatalogRuleException("The last active variant of an active product cannot be deactivated.");
    }
    public void ReplaceDetails(IReadOnlyList<ImageDetails> images, IReadOnlyList<SpecificationDetails> specifications, IReadOnlyDictionary<string, string> metadata)
    {
        if (images.Count > 30 || specifications.Count > 100 || metadata.Count > 50 ||
            images.Select(image => image.SortOrder).Distinct().Count() != images.Count || images.Count(image => image.IsPrimary) > 1)
            throw new CatalogRuleException("Product details exceed limits or image ordering/primary selection is duplicated.");
        var primaryOrder = images.Count == 0 ? -1 : images.SingleOrDefault(image => image.IsPrimary)?.SortOrder ?? images.Min(image => image.SortOrder);
        var newImages = images.Select(image => ProductImage.Create(Id, image, image.SortOrder == primaryOrder)).ToList();
        var newSpecifications = specifications.Select(specification => ProductSpecification.Create(Id, specification)).ToList();
        var newMetadata = metadata.Select(pair => ProductMetadata.Create(Id, pair.Key, pair.Value)).ToList();
        if (newSpecifications.Select(specification => specification.NormalizedName).Distinct().Count() != specifications.Count ||
            newMetadata.Select(item => item.NormalizedKey).Distinct().Count() != metadata.Count)
            throw new CatalogRuleException("Specification names and metadata keys must be unique within a product.");
        _images.Clear(); _images.AddRange(newImages);
        _specifications.Clear(); _specifications.AddRange(newSpecifications);
        _metadata.Clear(); _metadata.AddRange(newMetadata);
        Revision = Guid.NewGuid();
    }
    private static void ValidatePhysicalData(ProductKind kind, decimal? weight, Dimensions? dimensions)
    {
        if (weight is <= 0 or > 1_000_000_000 || (kind == ProductKind.Digital && (weight is not null || dimensions is not null)))
            throw new CatalogRuleException("Physical measurements must be positive and are not allowed on digital products.");
    }
}
