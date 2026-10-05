namespace MyOnlineShop.Catalog.Domain;

public sealed record AttributeOption(Guid AttributeId, Guid ValueId);
public sealed record ImageDetails(string Url, string? AltText, int SortOrder, bool IsPrimary);
public sealed record SpecificationDetails(string Name, string Value, string? Unit, int SortOrder);

public sealed class ProductAttributeOption
{
    private ProductAttributeOption() { }
    internal ProductAttributeOption(Guid productId, Guid attributeId, Guid valueId)
    { ProductId = productId; AttributeId = attributeId; ValueId = valueId; }
    public Guid ProductId { get; private set; }
    public Guid AttributeId { get; private set; }
    public Guid ValueId { get; private set; }
}

public sealed class ProductImage
{
    private ProductImage() { }
    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public string Url { get; private set; } = string.Empty;
    public string? AltText { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsPrimary { get; private set; }
    internal static ProductImage Create(Guid productId, ImageDetails details, bool primary)
    {
        if (details.Url.Length > 2048 || !Uri.TryCreate(details.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            details.SortOrder is < 0 or > 10000 || details.AltText?.Length > 200)
            throw new CatalogRuleException("Images require a valid HTTPS URL, ordering and optional alt text.");
        return new() { Id = Guid.NewGuid(), ProductId = productId, Url = details.Url, AltText = details.AltText,
            SortOrder = details.SortOrder, IsPrimary = primary };
    }
}

public sealed class ProductSpecification
{
    private ProductSpecification() { }
    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
    public string? Unit { get; private set; }
    public int SortOrder { get; private set; }
    internal static ProductSpecification Create(Guid productId, SpecificationDetails details)
    {
        var name = CatalogRules.Required(details.Name, 100, "Specification name");
        var value = CatalogRules.Required(details.Value, 1000, "Specification value");
        if (details.Unit?.Length > 32 || details.SortOrder is < 0 or > 10000)
            throw new CatalogRuleException("Specification unit or ordering is invalid.");
        return new() { Id = Guid.NewGuid(), ProductId = productId, Name = name, NormalizedName = name.ToUpperInvariant(),
            Value = value, Unit = details.Unit, SortOrder = details.SortOrder };
    }
}

public sealed class ProductMetadata
{
    private ProductMetadata() { }
    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string NormalizedKey { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
    internal static ProductMetadata Create(Guid productId, string key, string value)
    {
        var validKey = CatalogRules.Required(key, 100, "Metadata key");
        if (value is null || value.Length > 1000) throw new CatalogRuleException("Metadata value is invalid.");
        return new() { Id = Guid.NewGuid(), ProductId = productId, Key = validKey, NormalizedKey = validKey.ToUpperInvariant(), Value = value };
    }
}
