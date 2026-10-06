using System.Security.Cryptography;
using System.Text;
using MyOnlineShop.Pricing.Contracts;

namespace MyOnlineShop.Refund.Domain;

public enum RefundStatus { Pending, Processing, Completed, Failed }
public sealed class RefundRuleException(string message) : Exception(message);

public sealed class Refund
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid PaymentId { get; private set; }
    public Guid UserId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public Guid IdempotencyKey { get; private set; }
    public RefundStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset DueAtUtc { get; private set; }
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? ProcessedAtUtc { get; private set; }
    public Guid? WalletTransactionId { get; private set; }
    public int AttemptCount { get; private set; }
    public string? FailureCode { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public byte[] RowVersion { get; private set; } = [];
    public static Guid StableId(string purpose, Guid paymentId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{purpose}:{paymentId:N}")).AsSpan(0, 16));
    public static Refund Create(Guid order, Guid payment, Guid user, decimal amount, string currency,
        DateTimeOffset now, TimeSpan delay, string correlation)
    {
        if (order == Guid.Empty || payment == Guid.Empty || user == Guid.Empty || now == default || now.Offset != TimeSpan.Zero ||
            delay < TimeSpan.Zero || delay > TimeSpan.FromDays(30) || correlation is null || correlation.Length > 128)
            throw new RefundRuleException("Invalid refund identity or schedule.");
        currency = MoneyRules.Currency(currency); MoneyRules.Amount(amount, currency);
        return new() { Id = StableId("inventory-refund", payment), IdempotencyKey = StableId("inventory-refund-credit", payment),
            OrderId = order, PaymentId = payment, UserId = user, Amount = amount, Currency = currency, Reason = "StockUnavailable",
            Status = RefundStatus.Pending, CreatedAtUtc = now, DueAtUtc = now.Add(delay), NextAttemptAtUtc = now.Add(delay), CorrelationId = correlation };
    }
    public bool IsEligible(DateTimeOffset now) => Status == RefundStatus.Pending && now >= DueAtUtc && now >= NextAttemptAtUtc;
    public void Schedule(DateTimeOffset now)
    {
        if (!IsEligible(now)) throw new RefundRuleException("Refund is not eligible for processing.");
        Status = RefundStatus.Processing; AttemptCount++; FailureCode = null;
    }
    public void Complete(Guid ledger, DateTimeOffset now)
    {
        if (Status != RefundStatus.Processing || ledger == Guid.Empty || now < DueAtUtc || now.Offset != TimeSpan.Zero)
            throw new RefundRuleException("Invalid refund completion.");
        Status = RefundStatus.Completed; WalletTransactionId = ledger; ProcessedAtUtc = now; FailureCode = null;
    }
    public void Retry(DateTimeOffset now, TimeSpan backoff, int maximumAttempts, string failureCode)
    {
        if (Status != RefundStatus.Processing || backoff <= TimeSpan.Zero || maximumAttempts < 1 ||
            failureCode is not ("WalletCreditFailed" or "PaymentEvidenceUnavailable" or "PaymentEvidenceMismatch"))
            throw new RefundRuleException("Invalid refund failure transition.");
        FailureCode = failureCode;
        Status = failureCode == "PaymentEvidenceMismatch" || AttemptCount >= maximumAttempts ? RefundStatus.Failed : RefundStatus.Pending;
        NextAttemptAtUtc = now.Add(backoff);
    }
}
