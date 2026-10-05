using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Catalog.Application;
using MyOnlineShop.Catalog.Domain;

namespace MyOnlineShop.Catalog.Infrastructure.Persistence;

internal sealed class CatalogUnitOfWork(CatalogDbContext context) : ICatalogUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        try
        {
            return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(ct);
                var result = await action(ct);
                await context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return result;
            });
        }
        catch (CatalogRuleException error) { throw CatalogException.Invalid(error.Message); }
        catch (SqlException error) when (error.Number == 50002)
        { throw new CatalogException("catalog_concurrency", 409, "Catalog hierarchy is busy. Retry the operation."); }
        catch (DbUpdateConcurrencyException) { throw new CatalogException("catalog_concurrency", 409, "Catalog resource changed. Reload and retry."); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 }) { throw CatalogException.Conflict(); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 547 })
        { throw CatalogException.Invalid("Catalog references or constraints changed. Reload and retry."); }
    }
    public async Task LockCategoryHierarchyAsync(CancellationToken ct)
    {
        await context.Database.ExecuteSqlRawAsync("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource=N'CatalogCategoryHierarchy',
                @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 50002, 'Catalog hierarchy is busy.', 1;
            """, ct);
    }
}
