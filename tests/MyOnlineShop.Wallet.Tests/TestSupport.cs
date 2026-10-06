using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Wallet.Application;
using MyOnlineShop.Wallet.Contracts;
using MyOnlineShop.Wallet.Domain;
using MyOnlineShop.Wallet.Infrastructure;
using MyOnlineShop.Wallet.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Contracts;

namespace MyOnlineShop.Wallet.Tests;

internal sealed class FixedClock : TimeProvider
{
    public static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class RequestContext : IRequestContext { public string CorrelationId => "wallet-test"; }
internal sealed class IdentityReferences : IIdentityQueries
{
    public Guid User { get; } = Guid.NewGuid();
    public Guid Other { get; } = Guid.NewGuid();
    public bool Exists { get; set; } = true;
    public Task<UserDto> GetUserAsync(Guid userId, CancellationToken ct) =>
        Exists && (userId == User || userId == Other) ? Task.FromResult(new UserDto(userId, "First", "Last", "user@example.test", null, true, ["Customer"])) :
            throw new WalletException("wallet_owner", 404, "User not found.");
    public Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct) => throw new NotSupportedException();
}
// Explicit SQL-command substitute, backed by EF InMemory; no SQL locking/rollback claims.
internal sealed class MemoryWalletStore(WalletDbContext db) : IWalletStore
{
    private readonly WalletStore _reads = new(db);
    public Task<WalletAccount?> GetByUserAsync(Guid user, CancellationToken ct) => _reads.GetByUserAsync(user, ct);
    public Task<WalletAccount> CreateAsync(Guid user, string currency, DateTimeOffset at, CancellationToken ct) => _reads.CreateAsync(user, currency, at, ct);
    public Task<WalletLedger?> GetOperationAsync(Guid wallet, Guid key, CancellationToken ct) => _reads.GetOperationAsync(wallet, key, ct);
    public Task<bool> ReferenceExistsAsync(Guid wallet, WalletTransactionType type, string referenceType, string referenceId, CancellationToken ct) =>
        _reads.ReferenceExistsAsync(wallet, type, referenceType, referenceId, ct);
    public async Task<WalletBalanceChange?> ChangeBalanceAsync(Guid walletId, decimal delta, DateTimeOffset at, CancellationToken ct)
    {
        var wallet = await db.Wallets.SingleAsync(value => value.Id == walletId, ct);
        var after = wallet.Balance + delta;
        if (after < 0 || after > MoneyRules.MaximumAmount) return null;
        var change = new WalletBalanceChange(wallet.Id, wallet.Balance, after);
        db.Entry(wallet).Property(value => value.Balance).CurrentValue = after;
        db.Entry(wallet).Property(value => value.UpdatedAtUtc).CurrentValue = at;
        db.RecordBalanceChange(change); return change;
    }
    public void AddLedger(WalletLedger entry) => _reads.AddLedger(entry);
}
internal sealed class MemoryWalletUnit(WalletDbContext db) : IWalletUnitOfWork
{
    public Task LockAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        db.ResetPosting();
        try { var result = await action(ct); await db.SaveChangesAsync(ct); return result; }
        catch (WalletRuleException error) { db.ResetPosting(); throw WalletException.Invalid(error.Message); }
        catch (MoneyRuleException error) { db.ResetPosting(); throw WalletException.Invalid(error.Message); }
        catch { db.ResetPosting(); throw; }
    }
}
internal static class MemoryWallet
{
    public static void Configure(IServiceCollection services, IdentityReferences identity)
    {
        services.RemoveAll<WalletDbContext>(); services.RemoveAll<DbContextOptions<WalletDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<WalletDbContext>>();
        var name = Guid.NewGuid().ToString(); services.AddDbContext<WalletDbContext>(options => options.UseInMemoryDatabase(name));
        services.RemoveAll<IWalletStore>(); services.AddScoped<IWalletStore, MemoryWalletStore>();
        services.RemoveAll<IWalletUnitOfWork>(); services.AddScoped<IWalletUnitOfWork, MemoryWalletUnit>();
        services.RemoveAll<IIdentityQueries>(); services.AddSingleton<IIdentityQueries>(identity);
        services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider, FixedClock>();
    }
}
internal sealed class Harness : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    public IServiceProvider Services => _scope.ServiceProvider;
    public IdentityReferences Identity { get; } = new();
    public Guid Actor { get; } = Guid.NewGuid();
    public WalletDbContext Db => Services.GetRequiredService<WalletDbContext>();
    public IWalletOperations Operations => Services.GetRequiredService<IWalletOperations>();
    public IWalletQueries Queries => Services.GetRequiredService<IWalletQueries>();
    public Harness()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:SqlServer"] = "Server=localhost;Database=WalletOffline;Integrated Security=True" }).Build();
        var services = new ServiceCollection().AddLogging(); services.AddFoundationInfrastructure(config); services.AddWalletInfrastructure(config);
        MemoryWallet.Configure(services, Identity); services.AddSingleton<IRequestContext, RequestContext>();
        _provider = services.BuildServiceProvider(); _scope = _provider.CreateScope();
    }
    public WalletOperation Input(decimal amount = 500_000m, Guid? key = null, string currency = "IRR", string? reference = null) =>
        new() { Amount = amount, Currency = currency, IdempotencyKey = key ?? Guid.NewGuid(), ReferenceType = "Test",
            ReferenceId = reference ?? Guid.NewGuid().ToString("N"), Description = "Test posting" };
    public Task<WalletTransactionDto> Credit(WalletOperation? input = null) => Operations.CreditAsync(Identity.User, Actor, input ?? Input(), default);
    public Task<WalletTransactionDto> Debit(WalletOperation? input = null) => Operations.DebitAsync(Identity.User, Actor, input ?? Input(200_000m), default);
    public void Dispose() { _scope.Dispose(); _provider.Dispose(); }
}
