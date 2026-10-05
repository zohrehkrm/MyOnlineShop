using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Inventory.Application;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Inventory.Domain;
using MyOnlineShop.Inventory.Infrastructure;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Inventory.Tests;

public sealed class InventorySqlFactAttribute : FactAttribute
{
    public InventorySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INVENTORY_TEST_SQL_SERVER")))
            Skip = "SQL Server unavailable. Deferred to authorized Docker SQL Server using INVENTORY_TEST_SQL_SERVER.";
    }
}
internal sealed class InjectedAuditFailure : Exception;
internal sealed class AuditFailureInterceptor : SaveChangesInterceptor
{
    public bool Fail { get; set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Fail && eventData.Context!.ChangeTracker.Entries<InventoryMovement>().Any()) throw new InjectedAuditFailure();
        return ValueTask.FromResult(result);
    }
}
public sealed class InventorySqlFixture : IAsyncLifetime
{
    private readonly string? _master = Environment.GetEnvironmentVariable("INVENTORY_TEST_SQL_SERVER");
    private readonly string _database = "MyOnlineShop_InventoryTests_" + Guid.NewGuid().ToString("N");
    private bool _created;
    internal VariantReferences Catalog { get; } = new();
    internal AuditFailureInterceptor Fault { get; } = new();
    public ServiceProvider Provider { get; private set; } = null!;
    public Guid Actor { get; } = Guid.NewGuid();
    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_master)) return;
        var masterSettings = new SqlConnectionStringBuilder(_master);
        if (masterSettings.DataSource.Contains("localdb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LocalDB is prohibited. Use the authorized Docker SQL Server endpoint.");
        masterSettings.InitialCatalog = "master";
        await using var connection = new SqlConnection(masterSettings.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync(); _created = true;
        masterSettings.InitialCatalog = _database;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:SqlServer"] = masterSettings.ConnectionString }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddFoundationInfrastructure(configuration); services.AddInventoryInfrastructure(configuration);
        services.AddSingleton<ICatalogVariantReferences>(Catalog); services.AddSingleton<IRequestContext, TestRequest>();
        services.AddDbContext<InventoryDbContext>(options => options.AddInterceptors(Fault));
        Provider = services.BuildServiceProvider();
        using var scope = Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database.MigrateAsync();
    }
    public async Task DisposeAsync()
    {
        if (Provider is not null) await Provider.DisposeAsync();
        if (!_created) return;
        const string prefix = "MyOnlineShop_InventoryTests_";
        if (!_database.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(_database[prefix.Length..], "N", out _))
            throw new InvalidOperationException("Unsafe test database cleanup target.");
        SqlConnection.ClearAllPools();
        var settings = new SqlConnectionStringBuilder(_master) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(settings.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync();
    }
    public async Task<Guid> WarehouseAsync()
    {
        using var scope = Provider.CreateScope();
        var warehouse = await scope.ServiceProvider.GetRequiredService<IInventoryCommands>()
            .CreateWarehouseAsync(new() { Name = "SQL Test", Code = Guid.NewGuid().ToString("N") }, default);
        return warehouse.Id;
    }
    public StockOperationInput Input(Guid warehouse, long quantity, Guid? operation = null) => new()
    { OperationId = operation ?? Guid.NewGuid(), WarehouseId = warehouse, ProductVariantId = Catalog.Id,
        Quantity = quantity, Reference = "sql-business-reference", Reason = "SQL integration test" };
    public async Task<MovementDto> ReceiveAsync(StockOperationInput input)
    {
        using var scope = Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IInventoryCommands>().ReceiveAsync(input, Actor, default);
    }
    public async Task<MovementDto> DeductAsync(StockOperationInput input)
    {
        using var scope = Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IInventoryDeduction>().DeductAsync(input, Actor, default);
    }
    public async Task<StockDto> StockAsync(Guid warehouse)
    {
        using var scope = Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IInventoryQueries>().GetStockAsync(warehouse, Catalog.Id, default);
    }
    public async Task<IReadOnlyList<InventoryMovement>> MovementsAsync(Guid warehouse)
    {
        using var scope = Provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        return await context.Movements.AsNoTracking().Where(value => context.Stocks.Any(stock => stock.Id == value.StockId && stock.WarehouseId == warehouse)).ToListAsync();
    }
}
public sealed class InventorySqlTests(InventorySqlFixture fixture) : IClassFixture<InventorySqlFixture>
{
    [InventorySqlFact]
    public async Task Receipt_increases_stock_and_commits_movement_and_receipt_together()
    {
        var warehouse = await fixture.WarehouseAsync();
        var receipt = await fixture.ReceiveAsync(fixture.Input(warehouse, 5));
        Assert.Equal(5, (await fixture.StockAsync(warehouse)).Quantity);
        Assert.Equal(5, Assert.Single(await fixture.MovementsAsync(warehouse)).QuantityDelta);
        using var scope = fixture.Provider.CreateScope();
        var page = await scope.ServiceProvider.GetRequiredService<IInventoryQueries>().ListReceiptsAsync(new() { WarehouseId = warehouse }, default);
        Assert.Equal(receipt.Id, Assert.Single(page.Items).Movement.Id);
    }
    [InventorySqlFact]
    public async Task Receipt_failure_rolls_back_stock_creation_and_history()
    {
        var warehouse = await fixture.WarehouseAsync();
        fixture.Fault.Fail = true;
        try { await Assert.ThrowsAsync<InjectedAuditFailure>(() => fixture.ReceiveAsync(fixture.Input(warehouse, 5))); }
        finally { fixture.Fault.Fail = false; }
        using var scope = fixture.Provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.False(await context.Stocks.AnyAsync(value => value.WarehouseId == warehouse));
        Assert.Empty(await fixture.MovementsAsync(warehouse));
    }
    [InventorySqlFact]
    public async Task Deduction_failure_rolls_back_quantity_and_creates_no_sale_movement()
    {
        var warehouse = await fixture.WarehouseAsync();
        await fixture.ReceiveAsync(fixture.Input(warehouse, 2));
        fixture.Fault.Fail = true;
        try { await Assert.ThrowsAsync<InjectedAuditFailure>(() => fixture.DeductAsync(fixture.Input(warehouse, 1))); }
        finally { fixture.Fault.Fail = false; }
        Assert.Equal(2, (await fixture.StockAsync(warehouse)).Quantity);
        Assert.Single(await fixture.MovementsAsync(warehouse));
    }
    [InventorySqlFact]
    public async Task Concurrent_deductions_of_last_unit_have_exactly_one_winner()
    {
        var warehouse = await fixture.WarehouseAsync();
        await fixture.ReceiveAsync(fixture.Input(warehouse, 1));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Attempt()
        {
            await gate.Task;
            try { await fixture.DeductAsync(fixture.Input(warehouse, 1)); return true; }
            catch (InventoryException error) when (error.StatusCode == 409) { return false; }
        }
        var first = Attempt(); var second = Attempt(); gate.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, success => success); Assert.Single(results, success => !success);
        Assert.Equal(0, (await fixture.StockAsync(warehouse)).Quantity);
        Assert.Single(await fixture.MovementsAsync(warehouse), value => value.Type == MovementType.Sale);
    }
    [InventorySqlFact]
    public async Task Concurrent_duplicate_operation_changes_stock_and_records_sale_once()
    {
        var warehouse = await fixture.WarehouseAsync();
        await fixture.ReceiveAsync(fixture.Input(warehouse, 2));
        var input = fixture.Input(warehouse, 1);
        var results = await Task.WhenAll(fixture.DeductAsync(input), fixture.DeductAsync(input));
        Assert.Equal(results[0].Id, results[1].Id); Assert.Equal(1, (await fixture.StockAsync(warehouse)).Quantity);
        Assert.Single(await fixture.MovementsAsync(warehouse), value => value.Type == MovementType.Sale);
        await Assert.ThrowsAsync<InventoryException>(() => fixture.DeductAsync(fixture.Input(warehouse, 2, input.OperationId)));
    }
    [InventorySqlFact]
    public async Task Exact_and_insufficient_stock_deductions_preserve_nonnegative_quantity()
    {
        var warehouse = await fixture.WarehouseAsync();
        await fixture.ReceiveAsync(fixture.Input(warehouse, 3));
        await Assert.ThrowsAsync<InventoryException>(() => fixture.DeductAsync(fixture.Input(warehouse, 4)));
        Assert.Single(await fixture.MovementsAsync(warehouse));
        await fixture.DeductAsync(fixture.Input(warehouse, 3));
        Assert.Equal(0, (await fixture.StockAsync(warehouse)).Quantity);
    }
    [InventorySqlFact]
    public async Task Adjustment_and_return_commit_history_and_invalid_adjustment_does_not_change_stock()
    {
        var warehouse = await fixture.WarehouseAsync();
        await fixture.ReceiveAsync(fixture.Input(warehouse, 3));
        using var scope = fixture.Provider.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<IInventoryCommands>();
        StockAdjustmentInput Input(long delta) => new()
        { OperationId = Guid.NewGuid(), WarehouseId = warehouse, ProductVariantId = fixture.Catalog.Id,
            QuantityDelta = delta, Reference = "count-1", Reason = "Warehouse count" };
        await commands.AdjustAsync(Input(-1), fixture.Actor, default);
        await Assert.ThrowsAsync<InventoryException>(() => commands.AdjustAsync(Input(-3), fixture.Actor, default));
        await commands.ReturnAsync(fixture.Input(warehouse, 2), fixture.Actor, default);
        Assert.Equal(4, (await fixture.StockAsync(warehouse)).Quantity);
        Assert.Equal(3, (await fixture.MovementsAsync(warehouse)).Count);
        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Single(await context.Adjustments.Where(value => context.Movements.Any(movement => movement.Id == value.MovementId &&
            context.Stocks.Any(stock => stock.Id == movement.StockId && stock.WarehouseId == warehouse))).ToListAsync());
    }
    [InventorySqlFact]
    public async Task Concurrent_receipts_create_only_one_stock_record_and_history_is_append_only()
    {
        var warehouse = await fixture.WarehouseAsync();
        await Task.WhenAll(fixture.ReceiveAsync(fixture.Input(warehouse, 2)), fixture.ReceiveAsync(fixture.Input(warehouse, 3)));
        Assert.Equal(5, (await fixture.StockAsync(warehouse)).Quantity);
        using var scope = fixture.Provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Single(await context.Stocks.Where(value => value.WarehouseId == warehouse).ToListAsync());
        var movement = await context.Movements.FirstAsync(value => context.Stocks.Any(stock => stock.Id == value.StockId && stock.WarehouseId == warehouse));
        context.Movements.Remove(movement);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }
}
