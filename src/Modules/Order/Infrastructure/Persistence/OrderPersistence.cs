using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Order.Application;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Order.Domain;
using MyOnlineShop.Pricing.Contracts;
using OrderAggregate = MyOnlineShop.Order.Domain.Order;

namespace MyOnlineShop.Order.Infrastructure.Persistence;

public sealed class OrderStore(OrderDbContext context) : IOrderStore
{
    public Task<OrderAggregate?> GetAsync(Guid id, CancellationToken ct) => context.Orders.Include(value => value.Items).SingleOrDefaultAsync(value => value.Id == id, ct);
    public Task<OrderAggregate?> GetCheckoutAsync(Guid userId, Guid key, CancellationToken ct) =>
        context.Orders.Include(value => value.Items).SingleOrDefaultAsync(value => value.UserId == userId && value.IdempotencyKey == key, ct);
    public void Add(OrderAggregate order) => context.Orders.Add(order);
    public void Audit(OrderAudit audit) => context.Audit.Add(audit);
}
public sealed class OrderUnitOfWork(OrderDbContext context, IEnumerable<ILocalSqlTransactionParticipant> participants) : IOrderUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(bool includeCart, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        try
        {
            return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(ct);
                var cart = includeCart ? participants.SingleOrDefault(value => value.Name == "cart") ??
                    throw new InvalidOperationException("Cart transaction participant is required.") : null;
                try
                {
                    if (cart is not null) await cart.EnlistAsync(context.Database.GetDbConnection(), transaction.GetDbTransaction(), ct);
                    var result = await action(ct);
                    await context.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return result;
                }
                finally { if (cart is not null) await cart.DetachAsync(CancellationToken.None); }
            });
        }
        catch (OrderRuleException error) { throw OrderException.Invalid(error.Message); }
        catch (MoneyRuleException error) { throw OrderException.Invalid(error.Message); }
        catch (DbUpdateConcurrencyException) { throw OrderException.Conflict(); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 }) { throw OrderException.Conflict(); }
        catch (SqlException error) when (error.Number == 51007) { throw OrderException.Conflict("Checkout is busy. Retry with the same key."); }
    }
    public async Task LockCheckoutAsync(Guid userId, CancellationToken ct)
    {
        var resource = $"Checkout:{userId:N}";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 51007, 'Checkout is busy.', 1;
            """, ct);
    }
}
public sealed class OrderReadStore(OrderDbContext context) : IOrderReadStore
{
    private static System.Linq.Expressions.Expression<Func<OrderAggregate, OrderDto>> Detail => value =>
        new(value.Id, value.Status.ToString(), value.Currency, value.Subtotal, value.DiscountTotal, value.PayableAmount,
            value.CreatedAtUtc, value.UpdatedAtUtc, value.PricedAtUtc,
            value.Address == null ? null : new AddressDto(value.Address.Recipient, value.Address.Street, value.Address.City, value.Address.PostalCode, value.Address.CountryCode),
            value.Items.OrderBy(item => item.Id).Select(item => new OrderItemDto(item.Id, item.ProductVariantId, item.Sku, item.ProductName, item.ProductKind,
                item.PriceId, item.DiscountId, item.UnitPrice, item.UnitDiscount, item.DiscountAmount, item.FinalUnitPrice, item.Quantity, item.LineTotal)).ToList());
    public async Task<OrderDto> GetMyAsync(Guid userId, Guid orderId, CancellationToken ct)
    {
        OrderException.User(userId);
        return await context.Orders.AsNoTracking().Where(value => value.UserId == userId && value.Id == orderId).Select(Detail).SingleOrDefaultAsync(ct) ?? throw OrderException.NotFound();
    }
    public async Task<OrderDto> GetAsync(Guid orderId, CancellationToken ct) =>
        await context.Orders.AsNoTracking().Where(value => value.Id == orderId).Select(Detail).SingleOrDefaultAsync(ct) ?? throw OrderException.NotFound();
    public Task<OrderPage> ListMyAsync(Guid userId, int page, int size, CancellationToken ct)
    { OrderException.User(userId); return PageAsync(context.Orders.Where(value => value.UserId == userId), page, size, ct); }
    public Task<OrderPage> ListAsync(int page, int size, CancellationToken ct) => PageAsync(context.Orders, page, size, ct);
    private static async Task<OrderPage> PageAsync(IQueryable<OrderAggregate> query, int page, int size, CancellationToken ct)
    {
        if (page < 1 || size is < 1 or > 100 || ((long)page - 1) * size > int.MaxValue) throw OrderException.Invalid();
        query = query.AsNoTracking(); var count = await query.CountAsync(ct);
        var items = await query.OrderByDescending(value => value.CreatedAtUtc).ThenBy(value => value.Id).Skip((page - 1) * size).Take(size)
            .Select(value => new OrderSummaryDto(value.Id, value.Status.ToString(), value.Currency, value.Subtotal, value.DiscountTotal, value.PayableAmount, value.CreatedAtUtc)).ToListAsync(ct);
        return new(items, page, size, count);
    }
}
