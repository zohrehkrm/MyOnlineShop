using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.Cart.Application;
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Cart.Infrastructure;
using MyOnlineShop.Cart.Infrastructure.Persistence;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Inventory.Domain;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using Xunit;
using CartAggregate = MyOnlineShop.Cart.Domain.Cart;

namespace MyOnlineShop.Cart.Tests;

public sealed class CartSqlFactAttribute : FactAttribute
{
    public CartSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CART_TEST_SQL_SERVER")))
            Skip = "SQL Server unavailable. Deferred to authorized Docker SQL Server using CART_TEST_SQL_SERVER.";
    }
}
internal sealed class CartLoadGate
{
    public Guid UserId { get; set; }
    private int _arrived;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task ArriveAsync(Guid userId)
    {
        if (userId != UserId) return Task.CompletedTask;
        if (Interlocked.Increment(ref _arrived) == 2) _ready.TrySetResult();
        return _ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
internal sealed class GatedCartRepository(CartRepository store, CartLoadGate gate) : ICartRepository
{
    public async Task<CartAggregate?> GetActiveAsync(Guid userId, CancellationToken ct)
    { var cart = await store.GetActiveAsync(userId, ct); await gate.ArriveAsync(userId); return cart; }
    public void Add(CartAggregate cart) => store.Add(cart);
}
public sealed class CartSqlFixture : IAsyncLifetime
{
    private readonly string? _master = Environment.GetEnvironmentVariable("CART_TEST_SQL_SERVER");
    private readonly string _database = "MyOnlineShop_CartTests_" + Guid.NewGuid().ToString("N");
    private bool _created;
    public ServiceProvider Provider { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";
    internal VariantReferences Catalog { get; } = new();
    internal CartLoadGate Gate { get; } = new();
    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_master)) return;
        var settings = new SqlConnectionStringBuilder(_master) { InitialCatalog = "master" };
        if (settings.DataSource.Contains("localdb", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("LocalDB is prohibited.");
        await using var connection = new SqlConnection(settings.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync(); _created = true;
        settings.InitialCatalog = _database; ConnectionString = settings.ConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:SqlServer"] = ConnectionString }).Build();
        var services = new ServiceCollection().AddLogging(); services.AddFoundationInfrastructure(configuration);
        services.AddCartInfrastructure(configuration); services.AddSingleton<ICatalogVariantReferences>(Catalog);
        services.AddSingleton(Gate); services.AddScoped<CartRepository>(); services.AddScoped<ICartRepository, GatedCartRepository>();
        Provider = services.BuildServiceProvider();
        using var scope = Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CartDbContext>().Database.MigrateAsync();
        using var inventory = Inventory(); await inventory.Database.MigrateAsync();
    }
    public InventoryDbContext Inventory() => new(new DbContextOptionsBuilder<InventoryDbContext>()
        .UseSqlServer(ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "inventory")).Options);
    public async Task DisposeAsync()
    {
        if (Provider is not null) await Provider.DisposeAsync();
        if (!_created) return;
        const string prefix = "MyOnlineShop_CartTests_";
        if (!_database.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(_database[prefix.Length..], "N", out _))
            throw new InvalidOperationException("Unsafe test database cleanup target.");
        SqlConnection.ClearAllPools();
        var settings = new SqlConnectionStringBuilder(_master) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(settings.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync();
    }
    public async Task<CartDto> AddAsync(Guid user, int quantity)
    {
        using var scope = Provider.CreateScope(); return await scope.ServiceProvider.GetRequiredService<ICartCommands>()
            .AddAsync(user, new() { ProductVariantId = Catalog.Id, Quantity = quantity }, default);
    }
    public async Task<CartDto> ReadAsync(Guid user)
    {
        using var scope = Provider.CreateScope(); return await scope.ServiceProvider.GetRequiredService<ICartQueries>().GetCurrentAsync(user, default);
    }
}
public sealed class CartSqlTests(CartSqlFixture fixture) : IClassFixture<CartSqlFixture>
{
    [CartSqlFact]
    public async Task Add_duplicate_update_remove_clear_and_owner_isolation_persist_correctly()
    {
        var user = Guid.NewGuid(); var other = Guid.NewGuid();
        Assert.Null((await fixture.ReadAsync(user)).Id);
        var first = await fixture.AddAsync(user, 2); var item = Assert.Single(first.Items);
        var second = await fixture.AddAsync(user, 3); Assert.Equal(item.Id, Assert.Single(second.Items).Id);
        Assert.Equal(5, second.Items[0].Quantity);
        using var scope = fixture.Provider.CreateScope(); var commands = scope.ServiceProvider.GetRequiredService<ICartCommands>();
        await Assert.ThrowsAsync<CartException>(() => commands.UpdateQuantityAsync(other, item.Id, new() { Quantity = 2 }, default));
        await Assert.ThrowsAsync<CartException>(() => commands.RemoveAsync(other, item.Id, default));
        await commands.ClearAsync(other, default); Assert.Equal(5, Assert.Single((await fixture.ReadAsync(user)).Items).Quantity);
        await commands.UpdateQuantityAsync(user, item.Id, new() { Quantity = 7 }, default);
        Assert.Equal(7, Assert.Single((await fixture.ReadAsync(user)).Items).Quantity);
        await commands.RemoveAsync(user, item.Id, default); Assert.Empty((await fixture.ReadAsync(user)).Items);
        await fixture.AddAsync(user, 1); await commands.ClearAsync(user, default);
        Assert.Empty((await fixture.ReadAsync(user)).Items);
    }
    [CartSqlFact]
    public async Task Cart_add_and_quantity_above_stock_leave_inventory_and_movements_unchanged()
    {
        using var inventory = fixture.Inventory();
        var warehouse = Warehouse.Create("Cart inventory fixture", Guid.NewGuid().ToString("N"), true);
        var stock = Stock.Create(warehouse.Id, fixture.Catalog.Id);
        inventory.Warehouses.Add(warehouse); inventory.Stocks.Add(stock); await inventory.SaveChangesAsync();
        // Fixture-only setup; this is not an Inventory application operation.
        await inventory.Stocks.Where(value => value.Id == stock.Id).ExecuteUpdateAsync(update => update.SetProperty(value => value.Quantity, 1));
        var baselineMovements = await inventory.Movements.CountAsync();
        var user = Guid.NewGuid(); var cart = await fixture.AddAsync(user, 1);
        Assert.Equal(1, Assert.Single(cart.Items).Quantity);
        using var scope = fixture.Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ICartCommands>().UpdateQuantityAsync(user, cart.Items[0].Id, new() { Quantity = 5 }, default);
        Assert.Equal(1, await inventory.Stocks.Where(value => value.Id == stock.Id).Select(value => value.Quantity).SingleAsync());
        Assert.Equal(baselineMovements, await inventory.Movements.CountAsync());
    }
    [CartSqlFact]
    public async Task Concurrent_updates_loaded_at_same_revision_produce_one_conflict_without_partial_change()
    {
        var user = Guid.NewGuid(); var cart = await fixture.AddAsync(user, 1); fixture.Gate.UserId = user;
        async Task<bool> Update(int quantity)
        {
            using var scope = fixture.Provider.CreateScope();
            try { await scope.ServiceProvider.GetRequiredService<ICartCommands>().UpdateQuantityAsync(user, cart.Items[0].Id, new() { Quantity = quantity }, default); return true; }
            catch (CartException error) when (error.StatusCode == 409) { return false; }
        }
        bool[] results;
        try { results = await Task.WhenAll(Update(2), Update(3)); }
        finally { fixture.Gate.UserId = Guid.Empty; }
        Assert.Single(results, success => success); Assert.Single(results, success => !success);
        Assert.Contains(Assert.Single((await fixture.ReadAsync(user)).Items).Quantity, new[] { 2, 3 });
    }
    [CartSqlFact]
    public async Task Unique_constraints_reject_duplicate_active_carts_and_duplicate_variant_items()
    {
        var user = Guid.NewGuid(); var current = await fixture.AddAsync(user, 1);
        using var scope = fixture.Provider.CreateScope(); var context = scope.ServiceProvider.GetRequiredService<CartDbContext>();
        context.Carts.Add(CartAggregate.Create(user, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync()); context.ChangeTracker.Clear();
        // Bypass domain aggregation only to verify the actual SQL unique constraint.
        await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [cart].[Items] (Id, CartId, ProductVariantId, Quantity, CreatedAtUtc, UpdatedAtUtc)
            VALUES ({Guid.NewGuid()}, {current.Id!.Value}, {fixture.Catalog.Id}, 1, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow});
            """));
    }
    [CartSqlFact]
    public async Task Failed_combined_quantity_limit_does_not_mutate_cart()
    {
        var user = Guid.NewGuid(); await fixture.AddAsync(user, 999);
        await Assert.ThrowsAsync<CartException>(() => fixture.AddAsync(user, 1));
        Assert.Equal(999, Assert.Single((await fixture.ReadAsync(user)).Items).Quantity);
    }
}
