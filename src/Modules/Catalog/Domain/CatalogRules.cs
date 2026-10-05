namespace MyOnlineShop.Catalog.Domain;

public sealed class CatalogRuleException(string message) : Exception(message);

public static class CatalogRules
{
    public static string Required(string? value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximumLength)
            throw new CatalogRuleException($"{field} is required and must fit its length limit.");
        return value.Trim();
    }
    public static string Code(string? value)
    {
        var code = Required(value, 64, "Code");
        if (!code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            throw new CatalogRuleException("Codes may contain only ASCII letters, digits, dots, underscores or hyphens.");
        return code.ToUpperInvariant();
    }
    public static void Id(Guid id, string field)
    {
        if (id == Guid.Empty) throw new CatalogRuleException($"{field} is required.");
    }
}

public enum ProductStatus { Draft = 1, Active = 2, Archived = 3 }
public enum ProductKind { Physical = 1, Digital = 2 }

public sealed record Dimensions
{
    public decimal LengthMillimeters { get; private init; }
    public decimal WidthMillimeters { get; private init; }
    public decimal HeightMillimeters { get; private init; }
    public Dimensions(decimal lengthMillimeters, decimal widthMillimeters, decimal heightMillimeters)
    {
        if (lengthMillimeters <= 0 || widthMillimeters <= 0 || heightMillimeters <= 0 ||
            lengthMillimeters > 1_000_000 || widthMillimeters > 1_000_000 || heightMillimeters > 1_000_000)
            throw new CatalogRuleException("All dimensions must be positive and within supported limits.");
        LengthMillimeters = lengthMillimeters; WidthMillimeters = widthMillimeters; HeightMillimeters = heightMillimeters;
    }
}
