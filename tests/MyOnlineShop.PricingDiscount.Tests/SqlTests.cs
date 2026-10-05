using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Cart.Infrastructure;
using MyOnlineShop.Cart.Infrastructure.Persistence;
using MyOnlineShop.Discount.Contracts;
using MyOnlineShop.Discount.Infrastructure;
using MyOnlineShop.Discount.Infrastructure.Persistence;
using MyOnlineShop.Inventory.Domain;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Application;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Pricing.Domain;
using MyOnlineShop.Pricing.Infrastructure;
using MyOnlineShop.Pricing.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.PricingDiscount.Tests;

public sealed class PricingSqlFactAttribute : FactAttribute
{
    public PricingSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PRICING_TEST_SQL_SERVER")))
            Skip = "SQL Server unavailable; deferred SQL persistence/transaction/concurrency validation. Set PRICING_TEST_SQL_SERVER only for an authorized disposable SQL Server.";
    }
}
public sealed class PricingSqlFixture : IAsyncLifetime
{
    private readonly string? _server = Environment.GetEnvironmentVariable("PRICING_TEST_SQL_SERVER");
    private readonly string _database = "MyOnlineShop_PricingTests_" + Guid.NewGuid().ToString("N");
    private bool _created;
    public ServiceProvider Services { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";
    internal CatalogReferences Catalog { get; } = new();
    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_server)) return;
        var options = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" };
        if (options.DataSource.Contains("localdb", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("LocalDB is prohibited.");
        await using var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync(); _created = true;
        options.InitialCatalog = _database; ConnectionString = options.ConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:SqlServer"] = ConnectionString }).Build();
        var services = new ServiceCollection().AddLogging(); services.AddFoundationInfrastructure(configuration);
        services.AddPricingInfrastructure(configuration); services.AddDiscountInfrastructure(configuration); services.AddCartInfrastructure(configuration);
        services.AddSingleton<ICatalogVariantReferences>(Catalog); services.AddSingleton<TimeProvider, FixedClock>();
        services.AddScoped<IRequestContext, RequestContext>();
        Services = services.BuildServiceProvider();
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PricingDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<DiscountDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<CartDbContext>().Database.MigrateAsync();
        using var inventory = Inventory(); await inventory.Database.MigrateAsync();
    }
    public InventoryDbContext Inventory() => new(new DbContextOptionsBuilder<InventoryDbContext>().UseSqlServer(ConnectionString,
        sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "inventory")).Options);
    internal PriceInput Input(decimal amount = 1_000_000m, bool active = true, DateTimeOffset? from = null, DateTimeOffset? to = null, string currency = "IRR") =>
        new() { ProductVariantId = Catalog.Id, BasePrice = amount, Currency = currency, IsActive = active,
            EffectiveFromUtc = from ?? FixedClock.Now.AddDays(-1), EffectiveToUtc = to };
    public async Task DisposeAsync()
    {
        if (Services is not null) await Services.DisposeAsync();
        if (!_created) return;
        const string prefix = "MyOnlineShop_PricingTests_";
        if (!_database.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(_database[prefix.Length..], "N", out _))
            throw new InvalidOperationException("Unsafe test database cleanup target.");
        SqlConnection.ClearAllPools();
        var options = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync();
    }
}
public sealed class SqlTests(PricingSqlFixture fixture) : IClassFixture<PricingSqlFixture>
{
    [PricingSqlFact]
    public async Task Status_command_reads_current_state_after_waiting_for_schedule_lock()
    {
        using var firstScope = fixture.Services.CreateScope(); var first = firstScope.ServiceProvider;
        var actor = Guid.NewGuid();
        var created = await first.GetRequiredService<IPriceCommands>().CreateAsync(fixture.Input(active: false, currency: "EUR"), actor, default);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activate = first.GetRequiredService<IPriceUnitOfWork>().ExecuteAsync(async ct =>
        {
            await first.GetRequiredService<IPriceUnitOfWork>().LockScheduleAsync(fixture.Catalog.Id, "EUR", ct);
            locked.TrySetResult();
            await metadataRead.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            var price = await first.GetRequiredService<IPriceStore>().GetAsync(created.Id, ct);
            price!.SetActive(true);
            first.GetRequiredService<IPriceStore>().AddHistory(PriceHistory.Record(price, "Activated", actor, FixedClock.Now, "race"));
            return true;
        }, default);
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using var secondScope = fixture.Services.CreateScope(); var second = secondScope.ServiceProvider;
        var commands = new PriceCommands(new GatedScheduleStore(second.GetRequiredService<IPriceStore>(), metadataRead),
            second.GetRequiredService<IPriceUnitOfWork>(), fixture.Catalog, new FixedClock(), new RequestContext());
        var deactivate = commands.SetActiveAsync(created.Id, false, actor, default);
        await Task.WhenAll(activate, deactivate);
        using var check = fixture.Services.CreateScope();
        Assert.False((await check.ServiceProvider.GetRequiredService<IPriceQueries>().GetAsync(created.Id, default)).IsActive);
        var history = await check.ServiceProvider.GetRequiredService<IPriceQueries>().GetHistoryAsync(fixture.Catalog.Id, "EUR", 1, 20, default);
        Assert.False(history.Single(x => x.Action == "Deactivated").Snapshot.IsActive);
    }
    [PricingSqlFact]
    public async Task Current_prices_half_open_periods_updates_status_and_history_persist()
    {
        using var scope = fixture.Services.CreateScope(); var commands = scope.ServiceProvider.GetRequiredService<IPriceCommands>();
        var queries = scope.ServiceProvider.GetRequiredService<IPriceQueries>(); var actor = Guid.NewGuid();
        var first = await commands.CreateAsync(fixture.Input(currency: "AED", to: FixedClock.Now), actor, default);
        var second = await commands.CreateAsync(fixture.Input(800_000m, currency: "AED", from: FixedClock.Now), actor, default);
        Assert.Equal(first.Id, (await queries.GetCurrentAsync(fixture.Catalog.Id, "AED", FixedClock.Now.AddTicks(-1), default))!.Id);
        Assert.Equal(second.Id, (await queries.GetCurrentManyAsync([fixture.Catalog.Id], "AED", FixedClock.Now, default)).Single().Id);
        await commands.UpdateAsync(second.Id, fixture.Input(700_000m, currency: "AED", from: FixedClock.Now), actor, default);
        await commands.SetActiveAsync(second.Id, false, actor, default);
        Assert.Null(await queries.GetCurrentAsync(fixture.Catalog.Id, "AED", FixedClock.Now, default));
        await commands.SetActiveAsync(second.Id, true, actor, default);
        Assert.Equal(5, (await queries.GetHistoryAsync(fixture.Catalog.Id, "AED", 1, 20, default)).Count);
        var db = scope.ServiceProvider.GetRequiredService<PricingDbContext>();
        db.History.Remove(await db.History.FirstAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
    [PricingSqlFact]
    public async Task Concurrent_overlapping_price_creates_allow_exactly_one_writer()
    {
        async Task<bool> Attempt(DateTimeOffset from)
        {
            using var scope = fixture.Services.CreateScope();
            try { await scope.ServiceProvider.GetRequiredService<IPriceCommands>().CreateAsync(fixture.Input(currency: "GBP", from: from), Guid.NewGuid(), default); return true; }
            catch (PricingException error) when (error.StatusCode == 409) { return false; }
        }
        var results = await Task.WhenAll(Attempt(FixedClock.Now.AddDays(-2)), Attempt(FixedClock.Now.AddDays(-1)));
        Assert.Equal(1, results.Count(success => success));
        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<PricingDbContext>();
        Assert.Equal(1, await db.Prices.CountAsync(value => value.Currency == "GBP")); Assert.Equal(1, await db.History.CountAsync(value => value.Currency == "GBP"));
    }
    [PricingSqlFact]
    public async Task Flushed_price_and_history_roll_back_together_and_rowversion_detects_stale_writer()
    {
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PricingDbContext>();
        var price = VariantPrice.Create(fixture.Input(currency: "TRY")); var actor = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IPriceUnitOfWork>().ExecuteAsync<int>(async ct =>
        {
            db.Prices.Add(price); db.History.Add(PriceHistory.Record(price, "Created", actor, FixedClock.Now, "rollback"));
            await db.SaveChangesAsync(ct); throw new InvalidOperationException("Injected failure after flush.");
        }, default));
        db.ChangeTracker.Clear();
        Assert.False(await db.Prices.AnyAsync(x => x.Id == price.Id)); Assert.False(await db.History.AnyAsync(x => x.PriceId == price.Id));
        var created = await scope.ServiceProvider.GetRequiredService<IPriceCommands>().CreateAsync(fixture.Input(currency: "TRY"), actor, default);
        using var otherScope = fixture.Services.CreateScope(); var other = otherScope.ServiceProvider.GetRequiredService<PricingDbContext>();
        var stale = await other.Prices.SingleAsync(x => x.Id == created.Id);
        await scope.ServiceProvider.GetRequiredService<IPriceCommands>().UpdateAsync(created.Id, fixture.Input(900_000m, currency: "TRY"), actor, default);
        stale.SetActive(false); await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync());
    }
    [PricingSqlFact]
    public async Task SQL_discount_candidates_calculation_and_cart_preview_leave_stock_and_usage_unchanged()
    {
        using var scope = fixture.Services.CreateScope(); var actor = Guid.NewGuid();
        await scope.ServiceProvider.GetRequiredService<IPriceCommands>().CreateAsync(fixture.Input(), actor, default);
        var discounts = scope.ServiceProvider.GetRequiredService<DiscountDbContext>();
        var rule = MyOnlineShop.Discount.Domain.DiscountRule.Create(new()
        { Name = "SQL offer", Value = 20, Currency = "IRR", StartsAtUtc = FixedClock.Now.AddDays(-1), EndsAtUtc = FixedClock.Now.AddDays(1),
            ProductVariantId = fixture.Catalog.Id, UsageLimit = 1, MinimumOrderAmount = 2_000_000m }, actor, FixedClock.Now);
        discounts.Rules.Add(rule); await discounts.SaveChangesAsync();
        using var inventory = fixture.Inventory(); var warehouse = Warehouse.Create("Pricing fixture", "pricing-fixture", true);
        var stock = Stock.Create(warehouse.Id, fixture.Catalog.Id); inventory.Warehouses.Add(warehouse); inventory.Stocks.Add(stock);
        inventory.Entry(stock).Property(x => x.Quantity).CurrentValue = 1; await inventory.SaveChangesAsync();
        var cartCommands = scope.ServiceProvider.GetRequiredService<ICartCommands>();
        var cart = await cartCommands.AddAsync(actor, new() { ProductVariantId = fixture.Catalog.Id, Quantity = 1 }, default);
        cart = await cartCommands.UpdateQuantityAsync(actor, cart.Items.Single().Id, new() { Quantity = 5 }, default);
        var calc = scope.ServiceProvider.GetRequiredService<IPricingCalculation>();
        var quote = await calc.CalculateAsync(cart.Items.Select(x => new PriceLineRequest(x.ProductVariantId, x.Quantity)).ToArray(), "IRR", null, default);
        Assert.Equal(4_000_000m, quote.Lines.Single().TotalLineAmount);
        Assert.Equal(1, (await inventory.Stocks.AsNoTracking().SingleAsync(x => x.Id == stock.Id)).Quantity);
        Assert.Empty(await inventory.Movements.ToListAsync()); Assert.Equal(0, (await discounts.Rules.AsNoTracking().SingleAsync(x => x.Id == rule.Id)).UsedCount);
    }
}

internal sealed class GatedScheduleStore(IPriceStore inner, TaskCompletionSource metadataRead) : IPriceStore
{
    public async Task<(Guid VariantId, string Currency)?> GetScheduleAsync(Guid id, CancellationToken ct)
    { var result = await inner.GetScheduleAsync(id, ct); metadataRead.TrySetResult(); return result; }
    public Task<VariantPrice?> GetAsync(Guid id, CancellationToken ct) => inner.GetAsync(id, ct);
    public Task<bool> OverlapsAsync(VariantPrice price, CancellationToken ct) => inner.OverlapsAsync(price, ct);
    public void Add(VariantPrice price) => inner.Add(price);
    public void AddHistory(PriceHistory history) => inner.AddHistory(history);
}
