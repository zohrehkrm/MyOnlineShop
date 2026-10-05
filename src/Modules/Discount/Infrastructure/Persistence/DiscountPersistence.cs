using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Discount.Application;
using MyOnlineShop.Discount.Contracts;
using MyOnlineShop.Discount.Domain;
using MyOnlineShop.Pricing.Contracts;

namespace MyOnlineShop.Discount.Infrastructure.Persistence;

public sealed class DiscountStore(DiscountDbContext context) : IDiscountStore
{
    public Task<DiscountRule?> GetAsync(Guid id, CancellationToken ct) => context.Rules.SingleOrDefaultAsync(value => value.Id == id, ct);
    public void Add(DiscountRule rule) => context.Rules.Add(rule);
}
public sealed class DiscountUnitOfWork(DiscountDbContext context) : IDiscountUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        try
        {
            return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear(); await using var transaction = await context.Database.BeginTransactionAsync(ct);
                var result = await action(ct); await context.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return result;
            });
        }
        catch (DiscountRuleException error) { throw DiscountException.Invalid(error.Message); }
        catch (MoneyRuleException error) { throw DiscountException.Invalid(error.Message); }
        catch (DbUpdateConcurrencyException) { throw DiscountException.Conflict(); }
    }
}
public sealed class DiscountReadStore(DiscountDbContext context) : IDiscountQueries, IDiscountCandidates
{
    private static System.Linq.Expressions.Expression<Func<DiscountRule, DiscountRuleDto>> Projection => value =>
        new(value.Id, value.Name, value.Type, value.Value, value.Currency, value.IsActive, value.StartsAtUtc, value.EndsAtUtc,
            value.Priority, value.MinimumOrderAmount, value.UsageLimit, value.UsedCount, value.ProductVariantId, value.ProductId, value.CategoryId, value.CouponCode);
    public async Task<DiscountRuleDto> GetAsync(Guid id, CancellationToken ct) => await context.Rules.AsNoTracking().Where(value => value.Id == id)
        .Select(Projection).SingleOrDefaultAsync(ct) ?? throw DiscountException.NotFound();
    public async Task<IReadOnlyList<DiscountRuleDto>> ListAsync(int page, int size, CancellationToken ct)
    {
        if (page < 1 || size is < 1 or > 100 || ((long)page - 1) * size > int.MaxValue) throw DiscountException.Invalid();
        return await context.Rules.AsNoTracking().OrderByDescending(value => value.Priority).ThenBy(value => value.Id)
            .Skip((page - 1) * size).Take(size).Select(Projection).ToListAsync(ct);
    }
    public async Task<IReadOnlyList<DiscountRuleDto>> GetAsync(string currency, DateTimeOffset atUtc, string? couponCode, CancellationToken ct) =>
        await context.Rules.AsNoTracking().Where(value => value.Currency == currency && value.IsActive && value.StartsAtUtc <= atUtc && value.EndsAtUtc > atUtc &&
            (value.UsageLimit == null || value.UsedCount < value.UsageLimit) && (value.CouponCode == null || value.CouponCode == couponCode))
            .OrderByDescending(value => value.Priority).ThenBy(value => value.Id).Select(Projection).ToListAsync(ct);
}
