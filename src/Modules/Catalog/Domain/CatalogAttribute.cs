namespace MyOnlineShop.Catalog.Domain;

public sealed class CatalogAttribute
{
    private readonly List<AttributeValue> _values = [];
    private CatalogAttribute() { }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public Guid Revision { get; private set; }
    public IReadOnlyList<AttributeValue> Values => _values.AsReadOnly();
    public static CatalogAttribute Create(string name, string code, bool active)
    {
        var attribute = new CatalogAttribute { Id = Guid.NewGuid() }; attribute.Update(name, code, active); return attribute;
    }
    public void Update(string name, string code, bool active)
    {
        var validName = CatalogRules.Required(name, 100, "Attribute name");
        var validCode = CatalogRules.Code(code);
        Name = validName; Code = validCode; IsActive = active;
        Revision = Guid.NewGuid();
    }
    public AttributeValue AddValue(string label, string code, bool active)
    {
        var normalized = CatalogRules.Code(code);
        if (_values.Any(value => value.Code == normalized))
            throw new CatalogRuleException("An attribute value with this code already exists.");
        var value = AttributeValue.Create(Id, label, normalized, active); _values.Add(value); Revision = Guid.NewGuid(); return value;
    }
    public void UpdateValue(Guid valueId, string label, string code, bool active)
    {
        var value = _values.SingleOrDefault(value => value.Id == valueId)
            ?? throw new CatalogRuleException("The value does not belong to this attribute.");
        var normalized = CatalogRules.Code(code);
        if (_values.Any(other => other.Id != valueId && other.Code == normalized))
            throw new CatalogRuleException("An attribute value with this code already exists.");
        value.Update(label, normalized, active);
        Revision = Guid.NewGuid();
    }
}

public sealed class AttributeValue
{
    private AttributeValue() { }
    public Guid Id { get; private set; }
    public Guid AttributeId { get; private set; }
    public string Label { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    internal static AttributeValue Create(Guid attributeId, string label, string code, bool active)
    {
        var value = new AttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId };
        value.Update(label, code, active); return value;
    }
    internal void Update(string label, string code, bool active)
    {
        var validLabel = CatalogRules.Required(label, 200, "Attribute value label");
        var validCode = CatalogRules.Code(code);
        Label = validLabel; Code = validCode; IsActive = active;
    }
}
