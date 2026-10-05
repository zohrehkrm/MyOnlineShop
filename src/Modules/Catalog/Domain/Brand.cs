namespace MyOnlineShop.Catalog.Domain;

public sealed class Brand
{
    private Brand() { }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public static Brand Create(string name, string code, bool active)
    {
        var brand = new Brand { Id = Guid.NewGuid() }; brand.Update(name, code, active); return brand;
    }
    public void Update(string name, string code, bool active)
    {
        var validName = CatalogRules.Required(name, 100, "Brand name");
        var validCode = CatalogRules.Code(code);
        Name = validName; Code = validCode; IsActive = active;
    }
}
