using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Wallet.Contracts;

namespace MyOnlineShop.Wallet.Domain;

public enum WalletTransactionType { Credit, Debit }
public enum WalletTransactionStatus { Posted }
public sealed class WalletRuleException(string message) : Exception(message);
public sealed class WalletAccount
{
    private WalletAccount() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Currency { get; private set; } = "";
    public decimal Balance { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public static WalletAccount Create(Guid userId, string currency, DateTimeOffset now)
    {
        if (userId == Guid.Empty || now == default) throw new WalletRuleException("Wallet owner and timestamp are required.");
        return new() { Id = Guid.NewGuid(), UserId = userId, Currency = MoneyRules.Currency(currency), Balance = 0m,
            CreatedAtUtc = now.ToUniversalTime(), UpdatedAtUtc = now.ToUniversalTime() };
    }
}
public sealed record WalletBalanceChange(Guid WalletId, decimal Before, decimal After);
public sealed class WalletLedger
{
    private WalletLedger() { }
    public Guid Id { get; private set; }
    public Guid WalletId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "";
    public WalletTransactionType Type { get; private set; }
    public WalletTransactionStatus Status { get; private set; }
    public decimal BalanceBefore { get; private set; }
    public decimal BalanceAfter { get; private set; }
    public string ReferenceType { get; private set; } = "";
    public string ReferenceId { get; private set; } = "";
    public Guid IdempotencyKey { get; private set; }
    public string RequestFingerprint { get; private set; } = "";
    public string Description { get; private set; } = "";
    public Guid ActorId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public static WalletLedger Post(WalletBalanceChange change, WalletTransactionType type, WalletOperation input,
        string fingerprint, Guid actor, DateTimeOffset at, string correlation)
    {
        var currency = MoneyRules.Currency(input.Currency);
        MoneyRules.Amount(input.Amount, currency); MoneyRules.Amount(change.Before, currency, true); MoneyRules.Amount(change.After, currency, true);
        var signed = type switch { WalletTransactionType.Credit => input.Amount, WalletTransactionType.Debit => -input.Amount, _ => throw new WalletRuleException("Transaction type is invalid.") };
        if (change.WalletId == Guid.Empty || actor == Guid.Empty || input.IdempotencyKey == Guid.Empty || change.After != change.Before + signed ||
            fingerprint.Length != 64 || at == default || string.IsNullOrWhiteSpace(input.Description) || input.Description.Trim().Length > 500 ||
            string.IsNullOrWhiteSpace(input.ReferenceType) || input.ReferenceType.Length > 64 ||
            input.ReferenceType.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.') ||
            string.IsNullOrWhiteSpace(input.ReferenceId) || input.ReferenceId.Length > 128 || correlation.Length > 128)
            throw new WalletRuleException("Ledger amount, identity or reference is invalid.");
        return new() { Id = Guid.NewGuid(), WalletId = change.WalletId, Amount = signed, Currency = currency, Type = type,
            Status = WalletTransactionStatus.Posted, BalanceBefore = change.Before, BalanceAfter = change.After,
            ReferenceType = input.ReferenceType, ReferenceId = input.ReferenceId, IdempotencyKey = input.IdempotencyKey,
            RequestFingerprint = fingerprint, Description = input.Description.Trim(), ActorId = actor,
            CreatedAtUtc = at.ToUniversalTime(), CorrelationId = correlation };
    }
    public WalletTransactionDto Dto() => new(Id, WalletId, Amount, Currency, Type.ToString(), Status.ToString(),
        BalanceBefore, BalanceAfter, ReferenceType, ReferenceId, IdempotencyKey, Description, ActorId, CreatedAtUtc, CorrelationId);
}
