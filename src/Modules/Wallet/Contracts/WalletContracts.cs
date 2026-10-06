using System.ComponentModel.DataAnnotations;

namespace MyOnlineShop.Wallet.Contracts;

public sealed class AdminWalletCredit
{
    public Guid IdempotencyKey { get; init; }
    public decimal Amount { get; init; }
    [Required] public string Currency { get; init; } = "";
    [Required, StringLength(500)] public string Description { get; init; } = "";
}
// Trusted service contract only. Direction, status, balances and ownership are never taken from a public payload.
public sealed class WalletOperation
{
    public Guid IdempotencyKey { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "";
    public string ReferenceType { get; init; } = "";
    public string ReferenceId { get; init; } = "";
    public string Description { get; init; } = "";
}
public sealed class WalletTransactionsQuery
{
    [Range(1, int.MaxValue)] public int Page { get; init; } = 1;
    [Range(1, 100)] public int PageSize { get; init; } = 20;
    [RegularExpression("^(Credit|Debit)$")] public string? Type { get; init; }
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
}
public sealed record WalletDto(Guid Id, string Currency, decimal Balance, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record WalletTransactionDto(Guid Id, Guid WalletId, decimal Amount, string Currency, string Type, string Status,
    decimal BalanceBefore, decimal BalanceAfter, string ReferenceType, string ReferenceId, Guid IdempotencyKey,
    string Description, Guid ActorId, DateTimeOffset CreatedAtUtc, string CorrelationId);
public sealed record WalletTransactionsPage(IReadOnlyList<WalletTransactionDto> Items, int Page, int PageSize, int TotalCount);
public interface IWalletAdministration
{
    Task<WalletTransactionDto> CreditAsync(Guid actorId, Guid userId, AdminWalletCredit input, CancellationToken ct);
}
public interface IWalletOperations
{
    Task<WalletTransactionDto> CreditAsync(Guid userId, Guid actorId, WalletOperation operation, CancellationToken ct);
    Task<WalletTransactionDto> DebitAsync(Guid userId, Guid actorId, WalletOperation operation, CancellationToken ct);
}
public interface IWalletQueries
{
    Task<WalletDto> GetMyAsync(Guid userId, CancellationToken ct);
    Task<WalletTransactionsPage> GetMyTransactionsAsync(Guid userId, WalletTransactionsQuery query, CancellationToken ct);
}
