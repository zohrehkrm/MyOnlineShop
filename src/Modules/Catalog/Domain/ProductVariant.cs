using System.Security.Cryptography;
using System.Text;

namespace MyOnlineShop.Catalog.Domain;

public sealed class ProductVariant
{
    private readonly List<VariantAttributeValue> _values = [];
    private ProductVariant() { }
    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public string Sku { get; private set; } = string.Empty;
    public string CombinationHash { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public decimal? WeightGrams { get; private set; }
    public Dimensions? Dimensions { get; private set; }
    public IReadOnlyList<VariantAttributeValue> Values => _values.AsReadOnly();

    public static string HashCombination(IEnumerable<AttributeOption> values)
    {
        var canonical = string.Join(";", values.OrderBy(value => value.AttributeId)
            .Select(value => $"{value.AttributeId:N}:{value.ValueId:N}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
    internal static ProductVariant Create(Guid productId, string sku, bool active, decimal? weight, Dimensions? dimensions, IReadOnlyList<AttributeOption> values)
    {
        var variant = new ProductVariant { Id = Guid.NewGuid(), ProductId = productId };
        variant.Update(sku, active, weight, dimensions, values); return variant;
    }
    internal void Update(string sku, bool active, decimal? weight, Dimensions? dimensions, IReadOnlyList<AttributeOption> values)
    {
        var normalizedSku = CatalogRules.Code(sku);
        if (weight is <= 0 or > 1_000_000_000) throw new CatalogRuleException("Weight must be positive and within supported limits.");
        Sku = normalizedSku; IsActive = active; WeightGrams = weight; Dimensions = dimensions;
        CombinationHash = HashCombination(values);
        // Preserve tracked instances for selections whose composite keys remain unchanged.
        _values.RemoveAll(existing => !values.Any(value => value.AttributeId == existing.AttributeId));
        foreach (var selection in values)
        {
            var existing = _values.SingleOrDefault(value => value.AttributeId == selection.AttributeId);
            if (existing is null) _values.Add(new(ProductId, Id, selection.AttributeId, selection.ValueId));
            else existing.SetValue(selection.ValueId);
        }
    }
}

public sealed class VariantAttributeValue
{
    private VariantAttributeValue() { }
    internal VariantAttributeValue(Guid productId, Guid variantId, Guid attributeId, Guid valueId)
    { ProductId = productId; VariantId = variantId; AttributeId = attributeId; ValueId = valueId; }
    public Guid ProductId { get; private set; }
    public Guid VariantId { get; private set; }
    public Guid AttributeId { get; private set; }
    public Guid ValueId { get; private set; }
    internal void SetValue(Guid valueId) => ValueId = valueId;
}
