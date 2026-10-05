using Microsoft.EntityFrameworkCore;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Discount.Contracts;
using MyOnlineShop.Pricing.Application;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Pricing.Domain;
using MyOnlineShop.Pricing.Infrastructure.Persistence;

namespace MyOnlineShop.PricingDiscount.Tests;

internal sealed class FixedClock : TimeProvider
{
    public static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class RequestContext : IRequestContext { public string CorrelationId => "pricing-test"; }
internal sealed class CatalogReferences : ICatalogVariantReferences
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid ProductId { get; } = Guid.NewGuid();
    public Guid CategoryId { get; } = Guid.NewGuid();
    public bool Active { get; set; } = true;
    public Task<CatalogVariantReference?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(id == Id ? new CatalogVariantReference(Id, "PRICE-SKU", "Physical", Active, ProductId, CategoryId) : null);
}
internal sealed class Candidates : IDiscountCandidates
{
    public List<DiscountRuleDto> Rules { get; } = [];
    public Task<IReadOnlyList<DiscountRuleDto>> GetAsync(string currency, DateTimeOffset at, string? coupon, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DiscountRuleDto>>(Rules.ToArray());
}
// Test substitute only: does not claim to validate SQL transactions or application locks.
internal sealed class MemoryPriceUnit(PricingDbContext context) : IPriceUnitOfWork
{
    public Task LockScheduleAsync(Guid id, string currency, CancellationToken ct) => Task.CompletedTask;
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        context.ChangeTracker.Clear();
        try { var result = await action(ct); await context.SaveChangesAsync(ct); return result; }
        catch (PriceRuleException e) { throw PricingException.Invalid(e.Message); }
        catch (MoneyRuleException e) { throw PricingException.Invalid(e.Message); }
    }
}
internal sealed class PriceHarness : IDisposable
{
    public PricingDbContext Context { get; } = new(new DbContextOptionsBuilder<PricingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public CatalogReferences Catalog { get; } = new();
    public Candidates Discounts { get; } = new();
    public Guid Actor { get; } = Guid.NewGuid();
    public IPriceCommands Commands { get; }
    public IPriceQueries Queries { get; }
    public IPricingCalculation Calculation { get; }
    public PriceHarness()
    {
        Commands = new PriceCommands(new PriceStore(Context), new MemoryPriceUnit(Context), Catalog, new FixedClock(), new RequestContext());
        Queries = new PriceQueries(new PriceReadStore(Context));
        Calculation = new PricingCalculation(Queries, Discounts, Catalog, new FixedClock());
    }
    public PriceInput Input(decimal amount = 1_000_000m, bool active = true, DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? variant = null, string currency = "IRR") =>
        new() { ProductVariantId = variant ?? Catalog.Id, BasePrice = amount, Currency = currency, IsActive = active,
            EffectiveFromUtc = from ?? FixedClock.Now.AddDays(-1), EffectiveToUtc = to };
    public Task<PriceDto> Create(decimal amount = 1_000_000m) => Commands.CreateAsync(Input(amount), Actor, default);
    public Task<PricingQuote> Quote(int quantity = 1, string? coupon = null) => Calculation.CalculateAsync([new(Catalog.Id, quantity)], "IRR", coupon, default);
    public void Dispose() => Context.Dispose();
}
