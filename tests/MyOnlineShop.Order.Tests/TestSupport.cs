using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.Cart.Application;
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Cart.Infrastructure;
using MyOnlineShop.Cart.Infrastructure.Persistence;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Discount.Infrastructure;
using MyOnlineShop.Discount.Infrastructure.Persistence;
using MyOnlineShop.Inventory.Domain;
using MyOnlineShop.Inventory.Infrastructure;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using MyOnlineShop.Order.Application;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Order.Domain;
using MyOnlineShop.Order.Infrastructure;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Application;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Pricing.Infrastructure;
using MyOnlineShop.Pricing.Infrastructure.Persistence;

namespace MyOnlineShop.Order.Tests;

internal sealed class FixedClock : TimeProvider
{
    public static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class RequestContext : IRequestContext { public string CorrelationId => "order-test"; }
internal sealed class CatalogReferences : ICatalogVariantReferences
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid SecondId { get; } = Guid.NewGuid();
    public bool Active { get; set; } = true;
    public bool Exists { get; set; } = true;
    public string Name { get; set; } = "Original Product";
    public string Kind { get; set; } = "Physical";
    public Task<CatalogVariantReference?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<CatalogVariantReference?>(Exists && (id == Id || id == SecondId)
            ? new(id, id == Id ? "SKU-A" : "SKU-B", Kind, Active, Guid.NewGuid(), Guid.NewGuid(), Name) : null);
}
// Explicit test transaction substitutes; SQL atomicity is verified only by deferred SQL tests.
internal sealed class MemoryOrderUnit(OrderDbContext db, CartDbContext cart) : IOrderUnitOfWork
{
    public Task LockCheckoutAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    public async Task<T> ExecuteAsync<T>(bool includeCart, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        db.ChangeTracker.Clear(); cart.ChangeTracker.Clear();
        try { var result = await action(ct); await db.SaveChangesAsync(ct); return result; }
        catch (OrderRuleException error) { throw OrderException.Invalid(error.Message); }
        catch (MoneyRuleException error) { throw OrderException.Invalid(error.Message); }
    }
}
internal sealed class MemoryCartUnit(CartDbContext db) : ICartUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    { db.ChangeTracker.Clear(); var result = await action(ct); await db.SaveChangesAsync(ct); return result; }
}
internal sealed class MemoryPriceUnit(PricingDbContext db) : IPriceUnitOfWork
{
    public Task LockScheduleAsync(Guid id, string currency, CancellationToken ct) => Task.CompletedTask;
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    { db.ChangeTracker.Clear(); var result = await action(ct); await db.SaveChangesAsync(ct); return result; }
}
internal static class MemoryModules
{
    public static void Configure(IServiceCollection services, CatalogReferences catalog)
    {
        Replace<OrderDbContext>(services); Replace<CartDbContext>(services); Replace<PricingDbContext>(services);
        Replace<DiscountDbContext>(services); Replace<InventoryDbContext>(services);
        Replace<MessagingDbContext>(services);
        services.RemoveAll<IOutboxWriter>(); services.AddScoped<IOutboxWriter, MemoryOutboxWriter>();
        services.RemoveAll<IOrderUnitOfWork>(); services.AddScoped<IOrderUnitOfWork, MemoryOrderUnit>();
        services.RemoveAll<ICartUnitOfWork>(); services.AddScoped<ICartUnitOfWork, MemoryCartUnit>();
        services.RemoveAll<IPriceUnitOfWork>(); services.AddScoped<IPriceUnitOfWork, MemoryPriceUnit>();
        services.RemoveAll<ICatalogVariantReferences>(); services.AddSingleton<ICatalogVariantReferences>(catalog);
        services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider, FixedClock>();
    }
    private static void Replace<T>(IServiceCollection services) where T : DbContext
    {
        services.RemoveAll<T>(); services.RemoveAll<DbContextOptions<T>>(); services.RemoveAll<IDbContextOptionsConfiguration<T>>();
        var name = Guid.NewGuid().ToString(); services.AddDbContext<T>(options => options.UseInMemoryDatabase(name));
    }
    public static async Task SeedAsync(IServiceProvider services, CatalogReferences catalog, bool second = false, bool price = true, long stock = 5)
    {
        var inventory = services.GetRequiredService<InventoryDbContext>(); var warehouse = Warehouse.Create("Fixture", Guid.NewGuid().ToString("N"), true);
        inventory.Warehouses.Add(warehouse);
        foreach (var id in second ? new[] { catalog.Id, catalog.SecondId } : [catalog.Id])
        {
            var entry = Stock.Create(warehouse.Id, id); inventory.Stocks.Add(entry);
            inventory.Entry(entry).Property(value => value.Quantity).CurrentValue = stock;
            if (price) await services.GetRequiredService<IPriceCommands>().CreateAsync(new()
            { ProductVariantId = id, BasePrice = 1_000_000m, Currency = "IRR", EffectiveFromUtc = FixedClock.Now.AddDays(-1) }, Guid.NewGuid(), default);
        }
        await inventory.SaveChangesAsync();
    }
}
// Explicit test substitute; the production writer always requires an enlisted SQL transaction.
internal sealed class MemoryOutboxWriter(MessagingDbContext db) : IOutboxWriter
{
    public async Task EnqueueAsync(IIntegrationEvent message, string correlationId, CancellationToken ct)
    {
        var envelope = MessageEnvelope.From(message, correlationId);
        if (await db.Outbox.AnyAsync(value => value.Id == message.EventId, ct)) return;
        db.Outbox.Add(new() { Id = message.EventId, EventType = envelope.EventType, Version = envelope.Version,
            Payload = envelope.Payload.GetRawText(), Fingerprint = MessageFingerprint.Of(envelope), CorrelationId = correlationId,
            CreatedAtUtc = message.OccurredAtUtc, NextAttemptAtUtc = message.OccurredAtUtc });
        await db.SaveChangesAsync(ct);
    }
}
internal sealed class Harness : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    public IServiceProvider Services => _scope.ServiceProvider;
    public CatalogReferences Catalog { get; } = new();
    public Guid User { get; } = Guid.NewGuid();
    public Harness(Action<IServiceCollection>? additionalServices = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:SqlServer"] = "Server=localhost;Database=OrderOffline;Integrated Security=True" }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddFoundationInfrastructure(configuration); services.AddCartInfrastructure(configuration);
        services.AddPricingInfrastructure(configuration); services.AddDiscountInfrastructure(configuration);
        services.AddInventoryInfrastructure(configuration); services.AddOrderInfrastructure(configuration);
        MemoryModules.Configure(services, Catalog); services.AddSingleton<IRequestContext, RequestContext>();
        additionalServices?.Invoke(services);
        _provider = services.BuildServiceProvider(); _scope = _provider.CreateScope();
    }
    public Task Seed(bool second = false, bool price = true, long stock = 5) => MemoryModules.SeedAsync(Services, Catalog, second, price, stock);
    public Task<CartDto> Add(int quantity = 2, Guid? variant = null) => Services.GetRequiredService<ICartCommands>().AddAsync(User,
        new() { ProductVariantId = variant ?? Catalog.Id, Quantity = quantity }, default);
    public CheckoutInput Input(Guid? key = null, string currency = "IRR", string? coupon = null, AddressInput? address = null) =>
        new() { IdempotencyKey = key ?? Guid.NewGuid(), Currency = currency, CouponCode = coupon, Address = address };
    public Task<OrderDto> Checkout(CheckoutInput? input = null) => Services.GetRequiredService<ICheckoutCommands>().CreateAsync(User, input ?? Input(), default);
    public void Dispose() { _scope.Dispose(); _provider.Dispose(); }
}
