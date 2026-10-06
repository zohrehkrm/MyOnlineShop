using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Wallet.Application;
using MyOnlineShop.Wallet.Domain;

namespace MyOnlineShop.Wallet.Infrastructure.Persistence;

public sealed class WalletStore(WalletDbContext context) : IWalletStore
{
    public const string ConditionalBalanceSql = """
        UPDATE [wallet].[Wallets]
        SET [Balance] = [Balance] + @delta, [UpdatedAtUtc] = @now
        OUTPUT INSERTED.Id, DELETED.Balance, INSERTED.Balance
        WHERE [Id] = @wallet
          AND [Balance] + @delta >= 0 AND [Balance] + @delta <= 1000000000000;
        """;
    public Task<WalletAccount?> GetByUserAsync(Guid userId, CancellationToken ct) =>
        context.Wallets.AsNoTracking().SingleOrDefaultAsync(value => value.UserId == userId, ct);
    public async Task<WalletAccount> CreateAsync(Guid userId, string currency, DateTimeOffset now, CancellationToken ct)
    {
        var wallet = WalletAccount.Create(userId, currency, now); context.Wallets.Add(wallet);
        await context.SaveChangesAsync(ct); return wallet;
    }
    public Task<WalletLedger?> GetOperationAsync(Guid walletId, Guid key, CancellationToken ct) =>
        context.Ledger.AsNoTracking().SingleOrDefaultAsync(value => value.WalletId == walletId && value.IdempotencyKey == key, ct);
    public Task<bool> ReferenceExistsAsync(Guid walletId, WalletTransactionType type, string referenceType, string referenceId, CancellationToken ct) =>
        context.Ledger.AnyAsync(value => value.WalletId == walletId && value.Type == type && value.ReferenceType == referenceType && value.ReferenceId == referenceId, ct);
    public async Task<WalletBalanceChange?> ChangeBalanceAsync(Guid walletId, decimal delta, DateTimeOffset now, CancellationToken ct)
    {
        if (walletId == Guid.Empty || delta == 0 || delta < -MoneyRules.MaximumAmount || delta > MoneyRules.MaximumAmount) throw WalletException.Invalid();
        var transaction = context.Database.CurrentTransaction ?? throw new InvalidOperationException("Wallet posting requires a transaction.");
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction(); command.CommandTimeout = context.Database.GetCommandTimeout() ?? 30;
        command.CommandText = ConditionalBalanceSql;
        var amount = command.CreateParameter(); amount.ParameterName = "@delta"; amount.DbType = DbType.Decimal; amount.Precision = 18; amount.Scale = 4; amount.Value = delta; command.Parameters.Add(amount);
        var id = command.CreateParameter(); id.ParameterName = "@wallet"; id.DbType = DbType.Guid; id.Value = walletId; command.Parameters.Add(id);
        var timestamp = command.CreateParameter(); timestamp.ParameterName = "@now"; timestamp.DbType = DbType.DateTimeOffset; timestamp.Value = now.ToUniversalTime(); command.Parameters.Add(timestamp);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var change = new WalletBalanceChange(reader.GetGuid(0), reader.GetDecimal(1), reader.GetDecimal(2));
        if (await reader.ReadAsync(ct)) throw new InvalidOperationException("Wallet update affected multiple records.");
        context.RecordBalanceChange(change);
        return change;
    }
    public void AddLedger(WalletLedger entry) => context.Ledger.Add(entry);
}
public sealed class WalletUnitOfWork(WalletDbContext context) : IWalletUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        try
        {
            return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                context.ResetPosting();
                await using var transaction = await context.Database.BeginTransactionAsync(ct);
                var result = await action(ct);
                await context.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return result;
            });
        }
        catch (WalletRuleException error) { throw WalletException.Invalid(error.Message); }
        catch (MoneyRuleException error) { throw WalletException.Invalid(error.Message); }
        catch (DbUpdateConcurrencyException) { throw WalletException.Conflict(); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 }) { throw WalletException.Conflict("Wallet key or business reference has already been posted."); }
        catch (SqlException error) when (error.Number == 51009) { throw WalletException.Conflict("Wallet is busy. Retry with the same key."); }
    }
    public async Task LockAsync(Guid userId, CancellationToken ct)
    {
        var resource = $"Wallet:{userId:N}";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 51009, 'Wallet is busy.', 1;
            """, ct);
    }
}
