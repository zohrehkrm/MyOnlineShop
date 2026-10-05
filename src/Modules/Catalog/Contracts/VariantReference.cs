namespace MyOnlineShop.Catalog.Contracts;

public sealed record CatalogVariantReference(Guid Id, string Sku, string ProductKind, bool IsActive);
public interface ICatalogVariantReferences
{
    Task<CatalogVariantReference?> GetAsync(Guid variantId, CancellationToken cancellationToken);
}
