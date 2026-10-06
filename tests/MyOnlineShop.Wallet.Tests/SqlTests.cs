using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Wallet.Application;
using MyOnlineShop.Wallet.Contracts;
using MyOnlineShop.Wallet.Domain;
using MyOnlineShop.Wallet.Infrastructure;
using MyOnlineShop.Wallet.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Wallet.Tests;

public sealed class WalletSqlFactAttribute : FactAttribute
{
    public WalletSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WALLET_TEST_SQL_SERVER")))
            Skip = "SQL Server unavailable. Set WALLET_TEST_SQL_SERVER only for a future authorized disposable SQL Server test run.";
    }
}
public sealed class WalletSqlFixture : IAsyncLifetime
{
    private readonly string? _server = Environment.GetEnvironmentVariable("WALLET_TEST_SQL_SERVER");
    private readonly string _database = "MyOnlineShop_WalletTests_" + Guid.NewGuid().ToString("N");
    private bool _created;
    public ServiceProvider Provider { get; private set; } = null!;
    internal IdentityReferences Identity { get; } = new();
    public Guid Actor { get; } = Guid.NewGuid();
    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_server)) return;
        var options = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" };
        if (options.DataSource.Contains("localdb", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("LocalDB is prohibited.");
        await using var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync(); _created = true; options.InitialCatalog = _database;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:SqlServer"] = options.ConnectionString }).Build();
        var services = new ServiceCollection().AddLogging(); services.AddFoundationInfrastructure(config); services.AddWalletInfrastructure(config);
        services.AddSingleton<IIdentityQueries>(Identity); services.AddSingleton<TimeProvider, FixedClock>(); services.AddScoped<IRequestContext, RequestContext>();
        Provider = services.BuildServiceProvider(); using var scope = Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WalletDbContext>().Database.MigrateAsync();
    }
    public WalletOperation Input(decimal amount, Guid? key = null) => new()
    { Amount = amount, Currency = "IRR", IdempotencyKey = key ?? Guid.NewGuid(), ReferenceType = "SqlTest", ReferenceId = Guid.NewGuid().ToString("N"), Description = "SQL test posting" };
    public async Task<WalletTransactionDto> Post(Guid owner, WalletOperation input, bool debit = false)
    {
        using var scope = Provider.CreateScope(); var operations = scope.ServiceProvider.GetRequiredService<IWalletOperations>();
        return debit ? await operations.DebitAsync(owner, Actor, input, default) : await operations.CreditAsync(owner, Actor, input, default);
    }
    public async Task DisposeAsync()
    {
        if (Provider is not null) await Provider.DisposeAsync();
        if (!_created) return;
        const string prefix = "MyOnlineShop_WalletTests_";
        if (!_database.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(_database[prefix.Length..], "N", out _))
            throw new InvalidOperationException("Unsafe test database cleanup target.");
        SqlConnection.ClearAllPools(); var options = new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]";
        await command.ExecuteNonQueryAsync();
    }
}
public sealed class SqlTests(WalletSqlFixture fixture) : IClassFixture<WalletSqlFixture>
{
    [WalletSqlFact]
    public async Task Concurrent_two_700000_debits_from_1000000_allow_exactly_one_and_leave_300000()
    {
        var owner = fixture.Identity.User; await fixture.Post(owner, fixture.Input(1_000_000m));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Debit()
        {
            await start.Task;
            try { await fixture.Post(owner, fixture.Input(700_000m), true); return true; }
            catch (WalletException error) { Assert.Equal(409, error.StatusCode); Assert.Equal("Insufficient wallet balance.", error.Message); return false; }
        }
        var first = Debit(); var second = Debit(); start.SetResult(); var results = await Task.WhenAll(first, second);
        Assert.Single(results, success => success); Assert.Single(results, success => !success);
        using var scope = fixture.Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
        var wallet = await db.Wallets.AsNoTracking().SingleAsync(value => value.UserId == owner);
        Assert.Equal(300_000m, wallet.Balance);
        Assert.Equal(1, await db.Ledger.CountAsync(value => value.WalletId == wallet.Id && value.Type == WalletTransactionType.Debit && value.Status == WalletTransactionStatus.Posted));
        Assert.Equal(wallet.Balance, await db.Ledger.Where(value => value.WalletId == wallet.Id).SumAsync(value => value.Amount));
        Assert.All(await db.Ledger.Where(value => value.WalletId == wallet.Id).ToListAsync(), entry => Assert.True(entry.BalanceAfter >= 0));
    }
    [WalletSqlFact]
    public async Task Concurrent_initial_credits_and_duplicate_credit_debit_replays_post_once()
    {
        var owner = fixture.Identity.Other; var credit = fixture.Input(1_000_000m);
        var credits = await Task.WhenAll(fixture.Post(owner, credit), fixture.Post(owner, credit)); Assert.Equal(credits[0], credits[1]);
        var debit = fixture.Input(200_000m);
        var debits = await Task.WhenAll(fixture.Post(owner, debit, true), fixture.Post(owner, debit, true)); Assert.Equal(debits[0], debits[1]);
        Assert.Equal(credits[0], await fixture.Post(owner, credit));
        using var scope = fixture.Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
        var wallet = await db.Wallets.AsNoTracking().SingleAsync(value => value.UserId == owner);
        Assert.Equal(800_000m, wallet.Balance); Assert.Equal(2, await db.Ledger.CountAsync(value => value.WalletId == wallet.Id));
        var changed = fixture.Input(1m, credit.IdempotencyKey);
        Assert.Equal(409, (await Assert.ThrowsAsync<WalletException>(() => fixture.Post(owner, changed))).StatusCode);
    }
    [WalletSqlFact]
    public async Task Failure_after_balance_update_rolls_back_new_wallet_and_posts_no_ledger()
    {
        // Creation, conditional UPDATE and failure occur in one real SQL transaction.
        using var scope = fixture.Provider.CreateScope(); var services = scope.ServiceProvider;
        var db = services.GetRequiredService<WalletDbContext>(); var store = services.GetRequiredService<IWalletStore>(); var unit = services.GetRequiredService<IWalletUnitOfWork>();
        var user = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.ExecuteAsync<int>(async ct =>
        {
            await unit.LockAsync(user, ct); var wallet = await store.CreateAsync(user, "IRR", FixedClock.Now, ct);
            Assert.NotNull(await store.ChangeBalanceAsync(wallet.Id, 100m, FixedClock.Now, ct));
            throw new InvalidOperationException("Injected failure before ledger insertion.");
        }, default));
        db.ResetPosting(); Assert.False(await db.Wallets.AnyAsync(value => value.UserId == user)); Assert.Empty(await db.Ledger.Where(value => value.Amount == 100m).ToListAsync());
    }
    [WalletSqlFact]
    public async Task Failure_after_balance_and_ledger_flush_rolls_back_both_on_existing_wallet()
    {
        // Use a fixture-only zero wallet to keep this test independent of other postings.
        using var scope = fixture.Provider.CreateScope(); var services = scope.ServiceProvider;
        var db = services.GetRequiredService<WalletDbContext>(); var store = services.GetRequiredService<IWalletStore>(); var unit = services.GetRequiredService<IWalletUnitOfWork>();
        var user = Guid.NewGuid(); var wallet = WalletAccount.Create(user, "IRR", FixedClock.Now); db.Wallets.Add(wallet); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.ExecuteAsync<int>(async ct =>
        {
            await unit.LockAsync(user, ct); var change = (await store.ChangeBalanceAsync(wallet.Id, 100m, FixedClock.Now, ct))!;
            store.AddLedger(WalletLedger.Post(change, WalletTransactionType.Credit, fixture.Input(100m), new string('A', 64), fixture.Actor, FixedClock.Now, "rollback"));
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Injected failure after both flushes.");
        }, default));
        db.ResetPosting(); Assert.Equal(0m, (await db.Wallets.AsNoTracking().SingleAsync(value => value.Id == wallet.Id)).Balance);
        Assert.Empty(await db.Ledger.Where(value => value.WalletId == wallet.Id).ToListAsync());
    }
    [WalletSqlFact]
    public async Task SQL_enforces_primary_wallet_idempotency_and_business_reference_uniqueness()
    {
        using var scope = fixture.Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
        var user = Guid.NewGuid(); var wallet = WalletAccount.Create(user, "IRR", FixedClock.Now); db.Wallets.Add(wallet); await db.SaveChangesAsync();
        db.Wallets.Add(WalletAccount.Create(user, "IRR", FixedClock.Now));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); db.ResetPosting();
        var unit = scope.ServiceProvider.GetRequiredService<IWalletUnitOfWork>(); var store = scope.ServiceProvider.GetRequiredService<IWalletStore>();
        await unit.ExecuteAsync(async ct =>
        { await unit.LockAsync(user, ct); var change = (await store.ChangeBalanceAsync(wallet.Id, 50m, FixedClock.Now, ct))!;
          store.AddLedger(WalletLedger.Post(change, WalletTransactionType.Credit, fixture.Input(50m), new string('A', 64), fixture.Actor, FixedClock.Now, "unique")); return true; }, default);
        // Raw fixture inserts independently verify database constraints, bypassing the EF posting guard.
        foreach (var duplicateKey in new[] { true, false })
        {
            var key = Guid.NewGuid(); var reference = key.ToString("N"); var id = Guid.NewGuid();
            var failure = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [wallet].[Ledger] ([Id], [WalletId], [Amount], [Currency], [Type], [Status], [BalanceBefore], [BalanceAfter],
                  [ReferenceType], [ReferenceId], [IdempotencyKey], [RequestFingerprint], [Description], [ActorId], [CreatedAtUtc], [CorrelationId])
                SELECT {id}, [WalletId], [Amount], [Currency], [Type], [Status], [BalanceBefore], [BalanceAfter], [ReferenceType],
                  CASE WHEN {duplicateKey} = 1 THEN {reference} ELSE [ReferenceId] END,
                  CASE WHEN {duplicateKey} = 1 THEN [IdempotencyKey] ELSE {key} END,
                  [RequestFingerprint], [Description], [ActorId], [CreatedAtUtc], [CorrelationId] FROM [wallet].[Ledger] WHERE [WalletId] = {wallet.Id}
                """));
            Assert.True(failure.Number is 2601 or 2627);
        }
        Assert.Equal(50m, (await db.Wallets.AsNoTracking().SingleAsync(value => value.Id == wallet.Id)).Balance);
        Assert.Single(await db.Ledger.Where(value => value.WalletId == wallet.Id).ToListAsync());
    }
    [WalletSqlFact]
    public async Task SQL_trigger_rejects_historical_update_delete_and_foreign_key_prevents_wallet_deletion()
    {
        using var scope = fixture.Provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
        var user = Guid.NewGuid(); var wallet = WalletAccount.Create(user, "IRR", FixedClock.Now); db.Wallets.Add(wallet); await db.SaveChangesAsync();
        var store = scope.ServiceProvider.GetRequiredService<IWalletStore>(); var unit = scope.ServiceProvider.GetRequiredService<IWalletUnitOfWork>();
        await unit.ExecuteAsync(async ct =>
        { await unit.LockAsync(user, ct); var change = (await store.ChangeBalanceAsync(wallet.Id, 50m, FixedClock.Now, ct))!;
          store.AddLedger(WalletLedger.Post(change, WalletTransactionType.Credit, fixture.Input(50m), new string('A', 64), fixture.Actor, FixedClock.Now, "immutability")); return true; }, default);
        var update = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [wallet].[Ledger] SET [Description] = N'Rewritten' WHERE [WalletId] = {wallet.Id}"));
        Assert.Equal(51019, update.Number);
        var delete = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [wallet].[Ledger] WHERE [WalletId] = {wallet.Id}"));
        Assert.Equal(51019, delete.Number);
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [wallet].[Wallets] WHERE [Id] = {wallet.Id}"));
        Assert.Single(await db.Ledger.Where(value => value.WalletId == wallet.Id).ToListAsync());
    }
}
