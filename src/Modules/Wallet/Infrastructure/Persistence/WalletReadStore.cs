using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Wallet.Application;
using MyOnlineShop.Wallet.Contracts;
using MyOnlineShop.Wallet.Domain;

namespace MyOnlineShop.Wallet.Infrastructure.Persistence;

public sealed class WalletReadStore(WalletDbContext context) : IWalletQueries
{
    public async Task<WalletDto> GetMyAsync(Guid userId, CancellationToken ct)
    {
        WalletException.User(userId);
        return await context.Wallets.AsNoTracking().Where(value => value.UserId == userId)
            .Select(value => new WalletDto(value.Id, value.Currency, value.Balance, value.CreatedAtUtc, value.UpdatedAtUtc))
            .SingleOrDefaultAsync(ct) ?? throw WalletException.NotFound();
    }
    public async Task<WalletTransactionsPage> GetMyTransactionsAsync(Guid userId, WalletTransactionsQuery input, CancellationToken ct)
    {
        WalletException.User(userId);
        if (input.Page < 1 || input.PageSize is < 1 or > 100 || ((long)input.Page - 1) * input.PageSize > int.MaxValue ||
            input.Type is not (null or "Credit" or "Debit") || input.FromUtc >= input.ToUtc)
            throw WalletException.Invalid("Ledger filter or paging is invalid.");
        var walletId = await context.Wallets.AsNoTracking().Where(value => value.UserId == userId).Select(value => (Guid?)value.Id).SingleOrDefaultAsync(ct)
            ?? throw WalletException.NotFound();
        var query = context.Ledger.AsNoTracking().Where(value => value.WalletId == walletId);
        if (input.Type is not null)
        { var type = Enum.Parse<WalletTransactionType>(input.Type); query = query.Where(value => value.Type == type); }
        if (input.FromUtc is { } from) query = query.Where(value => value.CreatedAtUtc >= from);
        if (input.ToUtc is { } to) query = query.Where(value => value.CreatedAtUtc < to);
        var count = await query.CountAsync(ct);
        var items = await query.OrderByDescending(value => value.CreatedAtUtc).ThenBy(value => value.Id)
            .Skip((input.Page - 1) * input.PageSize).Take(input.PageSize)
            .Select(value => new WalletTransactionDto(value.Id, value.WalletId, value.Amount, value.Currency, value.Type.ToString(), value.Status.ToString(),
                value.BalanceBefore, value.BalanceAfter, value.ReferenceType, value.ReferenceId, value.IdempotencyKey, value.Description,
                value.ActorId, value.CreatedAtUtc, value.CorrelationId)).ToListAsync(ct);
        return new(items, input.Page, input.PageSize, count);
    }
}
