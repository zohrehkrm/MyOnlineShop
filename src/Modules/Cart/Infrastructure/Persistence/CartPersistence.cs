using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Cart.Application;
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Cart.Domain;
using CartAggregate = MyOnlineShop.Cart.Domain.Cart;

namespace MyOnlineShop.Cart.Infrastructure.Persistence;

public sealed class CartRepository(CartDbContext context) : ICartRepository
{
    public Task<CartAggregate?> GetActiveAsync(Guid userId, CancellationToken ct) =>
        context.Carts.Include(value => value.Items).SingleOrDefaultAsync(value => value.UserId == userId && value.IsActive, ct);
    public void Add(CartAggregate cart) => context.Carts.Add(cart);
}
public sealed class CartUnitOfWork(CartDbContext context) : ICartUnitOfWork
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
                await context.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return result;
            });
        }
        catch (CartRuleException error) { throw CartException.Invalid(error.Message); }
        catch (DbUpdateConcurrencyException) { throw CartException.Conflict(); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
        { throw CartException.Conflict(); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 547 })
        { throw CartException.Invalid("Cart references or quantity constraints are invalid."); }
    }
}
public sealed class CartReadStore(CartDbContext context) : ICartReadStore
{
    public async Task<CartDto> GetCurrentAsync(Guid userId, CancellationToken ct) =>
        await context.Carts.AsNoTracking().Where(value => value.UserId == userId && value.IsActive)
            .Select(value => new CartDto(value.Id, value.IsActive, value.CreatedAtUtc, value.UpdatedAtUtc,
                value.Items.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id)
                    .Select(item => new CartItemDto(item.Id, item.ProductVariantId, item.Quantity, item.CreatedAtUtc, item.UpdatedAtUtc)).ToList()))
            .SingleOrDefaultAsync(ct) ?? CartMapping.Empty();
}
