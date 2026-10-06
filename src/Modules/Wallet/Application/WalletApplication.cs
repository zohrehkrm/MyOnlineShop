using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Wallet.Contracts;
using MyOnlineShop.Wallet.Domain;

namespace MyOnlineShop.Wallet.Application;

public sealed class WalletException(string code, int status, string message) : Exception(message), IApplicationError
{
    public string Code => code;
    public int StatusCode => status;
    public string SafeMessage => Message;
    public static WalletException Invalid(string message = "Wallet input is invalid.") => new("wallet_validation", 400, message);
    public static WalletException NotFound() => new("wallet_not_found", 404, "Wallet not found.");
    public static WalletException Conflict(string message = "Wallet operation conflicts. Retry with the same key.") => new("wallet_conflict", 409, message);
    public static void User(Guid user) { if (user == Guid.Empty) throw new WalletException("wallet_user", 401, "A valid authenticated user is required."); }
}
public interface IWalletStore
{
    Task<WalletAccount?> GetByUserAsync(Guid userId, CancellationToken ct);
    Task<WalletAccount> CreateAsync(Guid userId, string currency, DateTimeOffset now, CancellationToken ct);
    Task<WalletLedger?> GetOperationAsync(Guid walletId, Guid key, CancellationToken ct);
    Task<bool> ReferenceExistsAsync(Guid walletId, WalletTransactionType type, string referenceType, string referenceId, CancellationToken ct);
    Task<WalletBalanceChange?> ChangeBalanceAsync(Guid walletId, decimal delta, DateTimeOffset now, CancellationToken ct);
    void AddLedger(WalletLedger entry);
}
public interface IWalletUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
    Task LockAsync(Guid userId, CancellationToken ct);
}
public sealed class WalletOperations(IWalletStore store, IWalletUnitOfWork unit, IIdentityQueries identity,
    TimeProvider clock, IRequestContext request) : IWalletOperations
{
    public Task<WalletTransactionDto> CreditAsync(Guid userId, Guid actor, WalletOperation input, CancellationToken ct) => PostAsync(userId, actor, input, WalletTransactionType.Credit, ct);
    public Task<WalletTransactionDto> DebitAsync(Guid userId, Guid actor, WalletOperation input, CancellationToken ct) => PostAsync(userId, actor, input, WalletTransactionType.Debit, ct);
    private async Task<WalletTransactionDto> PostAsync(Guid userId, Guid actor, WalletOperation input, WalletTransactionType type, CancellationToken ct)
    {
        WalletException.User(userId); WalletException.User(actor);
        string currency;
        try { currency = MoneyRules.Currency(input.Currency); MoneyRules.Amount(input.Amount, currency); }
        catch (MoneyRuleException error) { throw WalletException.Invalid(error.Message); }
        if (input.IdempotencyKey == Guid.Empty || string.IsNullOrWhiteSpace(input.Description) || input.Description.Trim().Length > 500 ||
            string.IsNullOrWhiteSpace(input.ReferenceType) || input.ReferenceType.Length > 64 ||
            input.ReferenceType.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.') ||
            string.IsNullOrWhiteSpace(input.ReferenceId) || input.ReferenceId.Length > 128)
            throw WalletException.Invalid("A key, positive amount, description and valid reference are required.");
        var normalized = new WalletOperation { IdempotencyKey = input.IdempotencyKey, Amount = input.Amount, Currency = currency,
            ReferenceType = input.ReferenceType, ReferenceId = input.ReferenceId, Description = input.Description.Trim() };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Type = type.ToString(), UserId = userId,
            ActorId = actor, Amount = normalized.Amount.ToString("G29", CultureInfo.InvariantCulture), normalized.Currency, normalized.ReferenceType, normalized.ReferenceId, normalized.Description })));
        return await unit.ExecuteAsync(async token =>
        {
            await unit.LockAsync(userId, token);
            var wallet = await store.GetByUserAsync(userId, token);
            if (wallet is not null)
            {
                var existing = await store.GetOperationAsync(wallet.Id, input.IdempotencyKey, token);
                if (existing is not null)
                {
                    if (existing.RequestFingerprint != fingerprint) throw WalletException.Conflict("Idempotency key was used for a different operation.");
                    return existing.Dto();
                }
            }
            await identity.GetUserAsync(userId, token);
            if (wallet is null)
            {
                if (type == WalletTransactionType.Debit) throw WalletException.NotFound();
                wallet = await store.CreateAsync(userId, currency, clock.GetUtcNow(), token);
            }
            if (wallet.Currency != currency) throw WalletException.Invalid("Wallet currency cannot change; currency conversion is not supported.");
            if (await store.ReferenceExistsAsync(wallet.Id, type, normalized.ReferenceType, normalized.ReferenceId, token))
                throw WalletException.Conflict("This business reference has already been posted.");
            var delta = type == WalletTransactionType.Credit ? input.Amount : -input.Amount;
            var change = await store.ChangeBalanceAsync(wallet.Id, delta, clock.GetUtcNow(), token)
                ?? throw WalletException.Conflict(type == WalletTransactionType.Debit ? "Insufficient wallet balance." : "Wallet balance limit exceeded.");
            var entry = WalletLedger.Post(change, type, normalized, fingerprint, actor, clock.GetUtcNow(), request.CorrelationId);
            store.AddLedger(entry);
            return entry.Dto();
        }, ct);
    }
}
public sealed class WalletAdministration(IWalletOperations operations) : IWalletAdministration
{
    // Authorized API actors are supplied by the existing permission policy; target ownership is explicit admin authority.
    public Task<WalletTransactionDto> CreditAsync(Guid actor, Guid userId, AdminWalletCredit input, CancellationToken ct)
    {
        if (userId == Guid.Empty) throw WalletException.Invalid("A valid target user is required.");
        return operations.CreditAsync(userId, actor, new() { IdempotencyKey = input.IdempotencyKey, Amount = input.Amount, Currency = input.Currency,
            ReferenceType = "AdministrativeCredit", ReferenceId = input.IdempotencyKey.ToString("N"), Description = input.Description }, ct);
    }
}
