using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Wallet.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Reporting.Tests;

public sealed class ReportingSqlFactAttribute : FactAttribute
{
    public ReportingSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REPORTING_TEST_SQL_SERVER")))
            Skip = "SQL Server unavailable; provide REPORTING_TEST_SQL_SERVER for read-only reports against an already migrated database.";
    }
}
public sealed class SqlIntegrationTests
{
    [ReportingSqlFact]
    public async Task Order_aggregates_and_paged_queries_execute_read_only_on_sql_server()
    {
        await using var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseSqlServer(Environment.GetEnvironmentVariable("REPORTING_TEST_SQL_SERVER")).Options);
        var source = new OrderReporting(db); var window = Harness.Window();
        var sales = await source.SalesAsync(window, default);
        Assert.All(sales, x => Assert.True(x.OrderCount > 0));
        Assert.True((await source.PeriodsAsync(window, "Day", 1, 10, default)).Items.Count <= 10);
        Assert.True((await source.PeriodsAsync(window, "Month", 1, 10, default)).Items.Count <= 10);
        Assert.True((await source.VariantsAsync(window, 1, 10, default)).Items.Count <= 10);
        Assert.True((await source.BuyersAsync(window, 1, 10, default)).Items.Count <= 10);
        Assert.True((await source.OrdersAsync(window, 1, 10, default)).Items.Count <= 10);
        Assert.Empty(db.ChangeTracker.Entries());
    }
    [ReportingSqlFact]
    public async Task Inventory_and_posted_ledger_aggregates_execute_read_only_on_sql_server()
    {
        var connection = Environment.GetEnvironmentVariable("REPORTING_TEST_SQL_SERVER");
        await using var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseSqlServer(connection).Options);
        Assert.True((await new InventoryReporting(db).MovementsAsync(Harness.Window(), null, 1, 10, default)).Items.Count <= 10);
        Assert.True((await new InventoryReporting(db).StockAsync(null, false, 1, 10, default)).Items.Count <= 10);
        await using var wallet = new WalletDbContext(new DbContextOptionsBuilder<WalletDbContext>().UseSqlServer(connection).Options);
        Assert.All(await new WalletReporting(wallet).SummaryAsync(Harness.Window(), default), x => Assert.True(x.Credits >= 0 && x.Debits >= 0));
        Assert.Empty(db.ChangeTracker.Entries()); Assert.Empty(wallet.ChangeTracker.Entries());
    }
}
