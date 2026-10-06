namespace MyOnlineShop.Catalog.Contracts;

public sealed record CatalogVariantReference(Guid Id, string Sku, string ProductKind, bool IsActive,
    Guid? ProductId = null, Guid? CategoryId = null, string? ProductName = null);
public interface ICatalogVariantReferences
{
    Task<CatalogVariantReference?> GetAsync(Guid variantId, CancellationToken cancellationToken);
    async Task<IReadOnlyList<CatalogVariantReference>> GetManyAsync(IReadOnlyList<Guid> variantIds, CancellationToken cancellationToken)
    {
        var values = new List<CatalogVariantReference>();
        foreach (var id in variantIds.Distinct())
        {
            var value = await GetAsync(id, cancellationToken);
            if (value is not null) values.Add(value);
        }
        return values;
    }
}
