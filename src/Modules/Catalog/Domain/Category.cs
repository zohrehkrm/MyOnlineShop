namespace MyOnlineShop.Catalog.Domain;

public sealed class Category
{
    private Category() { }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public Guid? ParentId { get; private set; }
    public bool IsActive { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public static Category Create(string name, string code, Guid? parentId, bool active)
    {
        var category = new Category { Id = Guid.NewGuid() };
        category.Update(name, code, parentId, active);
        return category;
    }
    public void Update(string name, string code, Guid? parentId, bool active)
    {
        var validName = CatalogRules.Required(name, 100, "Category name");
        var validCode = CatalogRules.Code(code);
        if (parentId == Id || parentId == Guid.Empty)
            throw new CatalogRuleException("A category cannot be its own parent or have an empty parent ID.");
        Name = validName; Code = validCode; ParentId = parentId; IsActive = active;
    }
    public static void ValidateParentChain(Guid categoryId, IEnumerable<Guid> ancestors)
    {
        var visited = new HashSet<Guid>();
        foreach (var ancestor in ancestors)
            if (ancestor == categoryId || !visited.Add(ancestor))
                throw new CatalogRuleException("Category hierarchy cannot contain a cycle.");
    }
}
