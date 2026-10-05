using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Domain;

namespace MyOnlineShop.Catalog.Application;

public interface ICatalogUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
    Task LockCategoryHierarchyAsync(CancellationToken ct);
}
public interface ICategoryRepository
{
    Task<Category?> GetAsync(Guid id, CancellationToken ct);
    Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken ct);
    Task<bool> HasActiveChildrenAsync(Guid id, CancellationToken ct);
    void Add(Category category);
}
public interface IBrandRepository
{
    Task<Brand?> GetAsync(Guid id, CancellationToken ct);
    Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken ct);
    void Add(Brand brand);
}
public interface IAttributeRepository
{
    Task<CatalogAttribute?> GetAsync(Guid id, CancellationToken ct);
    Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken ct);
    void Add(CatalogAttribute attribute);
}
public interface IProductRepository
{
    Task<Product?> GetAsync(Guid id, CancellationToken ct);
    Task<bool> SkuExistsAsync(string sku, Guid? excludingVariantId, CancellationToken ct);
    void Add(Product product);
}
public interface ICatalogReadStore : ICatalogQueries;
