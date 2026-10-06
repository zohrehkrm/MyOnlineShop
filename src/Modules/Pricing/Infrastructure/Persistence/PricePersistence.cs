using MyOnlineShop.BuildingBlocks.Infrastructure.Caching;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Pricing.Application;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Pricing.Domain;

namespace MyOnlineShop.Pricing.Infrastructure.Persistence;

public sealed class PriceStore(PricingDbContext context) : IPriceStore
{
    public async Task<(Guid VariantId, string Currency)?> GetScheduleAsync(Guid id, CancellationToken ct)
    {
        var schedule = await context.Prices.AsNoTracking().Where(value => value.Id == id)
            .Select(value => new { value.ProductVariantId, value.Currency }).SingleOrDefaultAsync(ct);
        return schedule is null ? null : (schedule.ProductVariantId, schedule.Currency);
    }
    public Task<VariantPrice?> GetAsync(Guid id, CancellationToken ct) => context.Prices.SingleOrDefaultAsync(value => value.Id == id, ct);
    public Task<bool> OverlapsAsync(VariantPrice price, CancellationToken ct) => context.Prices.AnyAsync(value =>
        value.Id != price.Id && value.ProductVariantId == price.ProductVariantId && value.Currency == price.Currency && value.IsActive &&
        (price.EffectiveToUtc == null || value.EffectiveFromUtc < price.EffectiveToUtc) &&
        (value.EffectiveToUtc == null || price.EffectiveFromUtc < value.EffectiveToUtc), ct);
    public void Add(VariantPrice price) => context.Prices.Add(price);
    public void AddHistory(PriceHistory history) => context.History.Add(history);
}
public sealed class PriceUnitOfWork(PricingDbContext context, ReadCache? cache = null) : IPriceUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        try
        {
            var committed = await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear(); await using var transaction = await context.Database.BeginTransactionAsync(ct);
                var result = await action(ct); await context.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return result;
            });
            if (cache is not null) await cache.InvalidateAsync("pricing");
            return committed;
        }
        catch (PriceRuleException error) { throw PricingException.Invalid(error.Message); }
        catch (MoneyRuleException error) { throw PricingException.Invalid(error.Message); }
        catch (DbUpdateConcurrencyException) { throw PricingException.Conflict(); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 }) { throw PricingException.Conflict(); }
        catch (SqlException error) when (error.Number == 51006) { throw PricingException.Conflict("Price schedule is busy. Retry."); }
    }
    public async Task LockScheduleAsync(Guid variantId, string currency, CancellationToken ct)
    {
        var resource = $"PriceSchedule:{variantId:N}:{currency}";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 51006, 'Price schedule is busy.', 1;
            """, ct);
    }
}
public sealed class PriceReadStore(PricingDbContext context, ReadCache? cache = null) : IPriceReadStore
{
    public Task<PriceDto> GetAsync(Guid id, CancellationToken ct) => cache is null ? DetailAsync(id, ct)
        : cache.GetAsync("pricing", MyOnlineShop.BuildingBlocks.Abstractions.CacheKeys.Detail("price", id, true), token => DetailAsync(id, token), ct);
    private static System.Linq.Expressions.Expression<Func<VariantPrice, PriceDto>> Projection => value =>
        new(value.Id, value.ProductVariantId, value.BasePrice, value.ComparePrice, value.Currency, value.IsActive, value.EffectiveFromUtc, value.EffectiveToUtc);
    public Task<PriceDto?> GetCurrentAsync(Guid variantId, string currency, DateTimeOffset atUtc, CancellationToken ct) =>
        context.Prices.AsNoTracking().Where(value => value.ProductVariantId == variantId && value.Currency == currency && value.IsActive &&
            value.EffectiveFromUtc <= atUtc && (value.EffectiveToUtc == null || value.EffectiveToUtc > atUtc)).Select(Projection).SingleOrDefaultAsync(ct);
    public async Task<IReadOnlyList<PriceDto>> GetCurrentManyAsync(IReadOnlyList<Guid> ids, string currency, DateTimeOffset atUtc, CancellationToken ct) =>
        await context.Prices.AsNoTracking().Where(value => ids.Contains(value.ProductVariantId) && value.Currency == currency && value.IsActive &&
            value.EffectiveFromUtc <= atUtc && (value.EffectiveToUtc == null || value.EffectiveToUtc > atUtc)).OrderBy(value => value.ProductVariantId).Select(Projection).ToListAsync(ct);
    private async Task<PriceDto> DetailAsync(Guid id, CancellationToken ct) => await context.Prices.AsNoTracking().Where(value => value.Id == id)
        .Select(Projection).SingleOrDefaultAsync(ct) ?? throw PricingException.NotFound();
    public async Task<IReadOnlyList<PriceHistoryDto>> GetHistoryAsync(Guid variantId, string currency, int page, int size, CancellationToken ct) =>
        await context.History.AsNoTracking().Where(value => value.ProductVariantId == variantId && value.Currency == currency)
            .OrderByDescending(value => value.AtUtc).ThenBy(value => value.Id).Skip((page - 1) * size).Take(size)
            .Select(value => new PriceHistoryDto(value.Id, value.PriceId, value.Action,
                new(value.PriceId, value.ProductVariantId, value.BasePrice, value.ComparePrice, value.Currency, value.IsActive, value.EffectiveFromUtc, value.EffectiveToUtc),
                value.ActorId, value.AtUtc, value.CorrelationId)).ToListAsync(ct);
}
