using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Pricing.Domain;

namespace MyOnlineShop.Pricing.Application;

public sealed class PricingException(string code, int status, string message) : Exception(message), IApplicationError
{
    public string Code => code;
    public int StatusCode => status;
    public string SafeMessage => Message;
    public static PricingException Invalid(string message = "Pricing input is invalid.") => new("pricing_validation", 400, message);
    public static PricingException NotFound() => new("price_not_found", 404, "Current price or price record not found.");
    public static PricingException Conflict(string message = "Price changed or overlaps an active price. Reload and retry.") => new("pricing_conflict", 409, message);
}
public static class PricingValidation
{
    public static string Currency(string currency)
    { try { return MoneyRules.Currency(currency); } catch (MoneyRuleException error) { throw PricingException.Invalid(error.Message); } }
    public static void Page(int page, int size)
    { if (page < 1 || size is < 1 or > 100 || ((long)page - 1) * size > int.MaxValue) throw PricingException.Invalid(); }
}
public interface IPriceStore
{
    Task<(Guid VariantId, string Currency)?> GetScheduleAsync(Guid id, CancellationToken ct);
    Task<VariantPrice?> GetAsync(Guid id, CancellationToken ct);
    Task<bool> OverlapsAsync(VariantPrice price, CancellationToken ct);
    void Add(VariantPrice price);
    void AddHistory(PriceHistory history);
}
public interface IPriceUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
    Task LockScheduleAsync(Guid variantId, string currency, CancellationToken ct);
}
public interface IPriceReadStore : IPriceQueries;
public sealed class PriceCommands(IPriceStore store, IPriceUnitOfWork unit, ICatalogVariantReferences catalog,
    TimeProvider clock, IRequestContext request) : IPriceCommands
{
    public Task<PriceDto> CreateAsync(PriceInput input, Guid actorId, CancellationToken ct) => SaveAsync(null, input, actorId, ct);
    public Task<PriceDto> UpdateAsync(Guid id, PriceInput input, Guid actorId, CancellationToken ct) => SaveAsync(id, input, actorId, ct);
    private async Task<PriceDto> SaveAsync(Guid? id, PriceInput input, Guid actorId, CancellationToken ct)
    {
        if (actorId == Guid.Empty || input.ProductVariantId == Guid.Empty) throw PricingException.Invalid();
        var currency = PricingValidation.Currency(input.Currency);
        return await unit.ExecuteAsync(async token =>
        {
            await unit.LockScheduleAsync(input.ProductVariantId, currency, token);
            if (await catalog.GetAsync(input.ProductVariantId, token) is null) throw PricingException.Invalid("Catalog variant does not exist.");
            var price = id is null ? VariantPrice.Create(input) : await store.GetAsync(id.Value, token) ?? throw PricingException.NotFound();
            price.Update(input);
            if (price.IsActive && await store.OverlapsAsync(price, token)) throw PricingException.Conflict();
            if (id is null) store.Add(price);
            store.AddHistory(PriceHistory.Record(price, id is null ? "Created" : "Updated", actorId, clock.GetUtcNow(), request.CorrelationId));
            return price.Dto();
        }, ct);
    }
    public async Task<PriceDto> SetActiveAsync(Guid id, bool active, Guid actorId, CancellationToken ct)
    {
        if (actorId == Guid.Empty) throw PricingException.Invalid();
        return await unit.ExecuteAsync(async token =>
        {
            var schedule = await store.GetScheduleAsync(id, token) ?? throw PricingException.NotFound();
            await unit.LockScheduleAsync(schedule.VariantId, schedule.Currency, token);
            var price = await store.GetAsync(id, token) ?? throw PricingException.NotFound();
            price.SetActive(active);
            if (active && await store.OverlapsAsync(price, token)) throw PricingException.Conflict();
            store.AddHistory(PriceHistory.Record(price, active ? "Activated" : "Deactivated", actorId, clock.GetUtcNow(), request.CorrelationId));
            return price.Dto();
        }, ct);
    }
}
public sealed class PriceQueries(IPriceReadStore store) : IPriceQueries
{
    public Task<PriceDto?> GetCurrentAsync(Guid id, string currency, DateTimeOffset at, CancellationToken ct)
    {
        if (id == Guid.Empty || at == default) throw PricingException.Invalid();
        return store.GetCurrentAsync(id, PricingValidation.Currency(currency), at.ToUniversalTime(), ct);
    }
    public Task<IReadOnlyList<PriceDto>> GetCurrentManyAsync(IReadOnlyList<Guid> ids, string currency, DateTimeOffset at, CancellationToken ct)
    {
        if (ids.Count > 100 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Count || at == default) throw PricingException.Invalid();
        return store.GetCurrentManyAsync(ids, PricingValidation.Currency(currency), at.ToUniversalTime(), ct);
    }
    public Task<IReadOnlyList<PriceHistoryDto>> GetHistoryAsync(Guid id, string currency, int page, int size, CancellationToken ct)
    { PricingValidation.Page(page, size); return store.GetHistoryAsync(id, PricingValidation.Currency(currency), page, size, ct); }
    public Task<PriceDto> GetAsync(Guid id, CancellationToken ct) => store.GetAsync(id, ct);
}
