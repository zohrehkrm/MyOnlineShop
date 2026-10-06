using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Infrastructure.Persistence;
using MyOnlineShop.Wallet.Application;
using MyOnlineShop.Wallet.Domain;
using MyOnlineShop.Wallet.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Wallet.Tests;

internal sealed class OfflineConnectionRequested : Exception;
internal sealed class BlockConnection : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData data, InterceptionResult result) => throw new OfflineConnectionRequested();
    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData data,
        InterceptionResult result, CancellationToken ct = default) => throw new OfflineConnectionRequested();
}
public sealed class ModelTests
{
    [Fact]
    public async Task SQL_model_migration_and_owner_projection_are_checked_without_database_access()
    {
        const string connection = "Server=localhost;Database=WalletOffline;Integrated Security=True";
        using var db = new WalletDbContext(new DbContextOptionsBuilder<WalletDbContext>().UseSqlServer(connection).AddInterceptors(new BlockConnection()).Options);
        Assert.Single(db.Database.GetMigrations()); Assert.False(db.Database.HasPendingModelChanges());
        Assert.All(db.Model.GetEntityTypes(), entity => Assert.Equal("wallet", entity.GetSchema()));
        Assert.All(db.Model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()), key =>
        { Assert.Equal("wallet", key.PrincipalEntityType.GetSchema()); Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior); });
        var sql = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("decimal(18,4)", sql); Assert.Contains("rowversion", sql); Assert.DoesNotContain("DROP TABLE", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Wallets_UserId]", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Ledger_WalletId_IdempotencyKey]", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Ledger_WalletId_Type_ReferenceType_ReferenceId]", sql);
        Assert.Contains("CREATE TRIGGER", sql); Assert.Contains("AFTER UPDATE, DELETE", sql); Assert.Contains("THROW 51019", sql);
        Assert.Contains("CK_Wallets_Balance", sql); Assert.Contains("[BalanceAfter] = [BalanceBefore] + [Amount]", sql);
        var query = new WalletReadStore(db);
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => query.GetMyAsync(Guid.NewGuid(), default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => query.GetMyTransactionsAsync(Guid.NewGuid(), new(), default));
        // The fully paged ledger projection also translates before connections are blocked.
        var readSql = db.Ledger.AsNoTracking().Where(value => value.WalletId == Guid.NewGuid() && value.Type == WalletTransactionType.Credit && value.CreatedAtUtc >= FixedClock.Now)
            .OrderByDescending(value => value.CreatedAtUtc).ThenByDescending(value => value.Id).Skip(20).Take(20)
            .Select(value => new { value.Id, value.Amount, value.BalanceBefore, value.BalanceAfter, value.ReferenceId }).ToQueryString();
        Assert.Contains("OFFSET", readSql); Assert.Contains("[wallet].[Ledger]", readSql);
        using var identity = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseSqlServer(connection).AddInterceptors(new BlockConnection()).Options);
        Assert.False(identity.Database.HasPendingModelChanges());
        var permissionSql = identity.GetService<IMigrator>().GenerateScript(fromMigration: identity.Database.GetMigrations().Reverse().Skip(1).First(),
            options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains(IdentityPermissions.CreditWallet, permissionSql); Assert.Contains("INSERT INTO", permissionSql); Assert.DoesNotContain("DROP TABLE", permissionSql);
        Assert.DoesNotContain("DELETE FROM", permissionSql);
    }
    [Fact]
    public void Money_is_decimal_and_module_dependencies_are_contracts_only()
    {
        Assert.DoesNotContain(typeof(WalletOperations).Assembly.GetReferencedAssemblies(), assembly =>
            assembly.Name!.Contains("Infrastructure") || assembly.Name.Contains("Presentation") || assembly.Name.Contains("Payment") || assembly.Name.Contains("Order") || assembly.Name.Contains("Inventory"));
        foreach (var entity in new[] { typeof(WalletAccount), typeof(WalletLedger) })
        {
            Assert.DoesNotContain(entity.GetProperties(), property => property.PropertyType == typeof(float) || property.PropertyType == typeof(double));
            Assert.All(entity.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic ?? false));
        }
        Assert.Contains("AND [Balance] + @delta >= 0", WalletStore.ConditionalBalanceSql);
        Assert.Contains("OUTPUT INSERTED.Id, DELETED.Balance, INSERTED.Balance", WalletStore.ConditionalBalanceSql);
    }
}
