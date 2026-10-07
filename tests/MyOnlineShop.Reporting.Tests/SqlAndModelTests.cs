using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Infrastructure.Persistence;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Reporting.Application;
using MyOnlineShop.Reporting.Contracts;
using MyOnlineShop.Wallet.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Reporting.Tests;

public sealed class SqlAndModelTests
{
    private const string Offline = "Server=localhost;Database=ReportingOffline;Integrated Security=True;TrustServerCertificate=True";
    [Fact]
    public void SQL_generation_uses_server_aggregates_filters_and_bounded_paging_without_a_connection()
    {
        using var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseSqlServer(Offline).Options);
        var source = new OrderReporting(db); var window = Harness.Window();
        var sales = source.SalesQuery(window).ToQueryString();
        Assert.Contains("GROUP BY", sales); Assert.Contains("SUM(", sales); Assert.Contains("AVG(", sales); Assert.Contains("COUNT_BIG", sales);
        Assert.Contains("[CreatedAtUtc] >=", sales); Assert.Contains("[CreatedAtUtc] <", sales);
        var periods = source.PeriodQuery(window, "Day").Skip(20).Take(20).ToQueryString();
        Assert.Contains("DATEPART", periods); Assert.Contains("OFFSET", periods); Assert.Contains("FETCH NEXT", periods);
        var month = source.PeriodQuery(window, "Month").ToQueryString(); Assert.Contains("GROUP BY", month);
        var variants = source.VariantQuery(window).Skip(0).Take(20).ToQueryString();
        Assert.Contains("JOIN", variants); Assert.Contains("SUM(", variants); Assert.Contains("DISTINCT", variants);
        Assert.Contains("[ordering].[Items]", variants); Assert.DoesNotContain("[catalog]", variants); Assert.DoesNotContain("[pricing]", variants);
        var buyers = source.BuyerQuery(window).Skip(0).Take(20).ToQueryString(); Assert.Contains("GROUP BY", buyers); Assert.Contains("FETCH NEXT", buyers);
        using var inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseSqlServer(Offline).Options);
        var movements = new InventoryReporting(inventory).MovementQuery(window, null).Skip(0).Take(20).ToQueryString();
        Assert.Contains("GROUP BY", movements); Assert.Contains("SUM(", movements); Assert.Contains("CASE", movements);
        using var wallet = new WalletDbContext(new DbContextOptionsBuilder<WalletDbContext>().UseSqlServer(Offline).Options);
        var ledger = new WalletReporting(wallet).SummaryQuery(window).ToQueryString(); Assert.Contains("GROUP BY", ledger); Assert.Contains("SUM(", ledger); Assert.Contains("InventoryRefund", ledger);
        Assert.Empty(db.ChangeTracker.Entries()); // ToQueryString compiles SQL only; no server is accessed.
    }
    [Fact]
    public void Reporting_contracts_have_no_persistence_or_business_entity_dependencies()
    {
        var assemblies = new[] { typeof(IReportingQueries).Assembly, typeof(ReportingQueries).Assembly };
        foreach (var assembly in assemblies)
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name!.Contains("Infrastructure") || reference.Name.Contains("EntityFrameworkCore"));
        Assert.DoesNotContain(typeof(DashboardDto).GetProperties(), property => property.PropertyType.Name.Contains("DbContext"));
    }
    [Fact]
    public void Incremental_permission_migration_matches_model_and_grants_only_existing_administrator_role()
    {
        using var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseSqlServer(Offline).Options);
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Contains(db.Database.GetMigrations(), x => x.EndsWith("AddReportingPermissions"));
        var model = db.GetService<IDesignTimeModel>().Model;
        var seeds = model.GetEntityTypes().Single(x => x.GetTableName() == "RolePermissions").GetSeedData();
        foreach (var permission in new[] { IdentityPermissions.ViewReports, IdentityPermissions.ReportSales, IdentityPermissions.ReportInventory, IdentityPermissions.ReportCustomers, IdentityPermissions.ReportFinancial })
        {
            var grant = Assert.Single(seeds, x => (string)x["PermissionKey"]! == permission);
            Assert.Equal(IdentityPermissions.AdministratorRoleId, grant["RoleId"]);
        }
    }
}
