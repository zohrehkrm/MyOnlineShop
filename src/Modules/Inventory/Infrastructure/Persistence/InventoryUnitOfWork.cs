using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Inventory.Application;
using MyOnlineShop.Inventory.Domain;

namespace MyOnlineShop.Inventory.Infrastructure.Persistence;

public sealed class InventoryUnitOfWork(InventoryDbContext context) : IInventoryUnitOfWork
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
        catch (InventoryRuleException error) { throw InventoryException.Invalid(error.Message); }
        catch (DbUpdateConcurrencyException) { throw InventoryException.Conflict("Inventory resource changed. Reload and retry."); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
        { throw InventoryException.Conflict("Inventory record or operation already exists."); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 547 })
        { throw InventoryException.Invalid("Inventory references or constraints are invalid."); }
        catch (SqlException error) when (error.Number == 51004)
        { throw InventoryException.Conflict("Inventory operation is busy. Retry using the same operation ID."); }
    }
    public async Task LockAsync(string resource, CancellationToken ct)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive',
                @LockOwner=N'Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 51004, 'Inventory operation is busy.', 1;
            """, ct);
    }
}
