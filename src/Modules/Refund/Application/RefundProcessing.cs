using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Refund.Contracts;
using MyOnlineShop.Refund.Domain;
using MyOnlineShop.Wallet.Contracts;
using RefundAggregate = MyOnlineShop.Refund.Domain.Refund;

namespace MyOnlineShop.Refund.Application;

public interface IRefundStore
{
    Task<RefundAggregate?> GetByPaymentAsync(Guid paymentId, CancellationToken ct);
    Task<RefundAggregate?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Guid>> DueAsync(DateTimeOffset now, int count, CancellationToken ct);
    void Add(RefundAggregate refund);
    Task SaveAsync(CancellationToken ct);
}
public interface IRefundUnitOfWork
{
    Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct);
    Task LockAsync(Guid paymentId, CancellationToken ct);
}
public sealed class RefundDependencyUnavailableException() : Exception("Verified Payment evidence is unavailable; Phase 8 integration is required.");

public sealed class RefundRequests(IRefundStore store, IRefundUnitOfWork unit, IOrderRefundSnapshots orders,
    IOutboxWriter outbox, IOptions<RefundProcessingOptions> options, TimeProvider clock)
{
    public Task RequestAsync(Guid paymentId, Guid orderId, string correlation, CancellationToken ct) => unit.ExecuteAsync(async token =>
    {
        if (paymentId == Guid.Empty || orderId == Guid.Empty) throw new InvalidMessageException();
        await unit.LockAsync(paymentId, token);
        var existing = await store.GetByPaymentAsync(paymentId, token);
        if (existing is not null)
        {
            if (existing.OrderId != orderId) throw new InvalidMessageException();
            return; // Preserve original amount, owner, schedule and completion on repeated failure messages.
        }
        var order = await orders.GetAsync(orderId, token) ?? throw new InvalidMessageException();
        if (order.OrderId != orderId) throw new InvalidMessageException();
        // This immutable order snapshot defines the candidate FULL refund, not proof that payment succeeded.
        var refund = RefundAggregate.Create(orderId, paymentId, order.UserId, order.PayableAmount, order.Currency,
            clock.GetUtcNow(), TimeSpan.FromMinutes(options.Value.DelayMinutes), correlation);
        store.Add(refund); await store.SaveAsync(token);
        await outbox.EnqueueAsync(new RefundRequestedIntegrationEventV1(RefundAggregate.StableId("refund-requested", paymentId),
            refund.CreatedAtUtc, refund.Id, orderId, paymentId), correlation, token);
    }, ct);
}

public sealed class RefundScheduler(IRefundStore store, IRefundUnitOfWork unit, IOutboxWriter outbox,
    IEnumerable<IRefundPaymentEvidence> evidence, IOptions<RefundProcessingOptions> options, TimeProvider clock)
{
    public bool HasPaymentEvidence => evidence.Count() == 1;
    public async Task<int> ScheduleAsync(CancellationToken ct)
    {
        // Keep durable requests Pending until an authoritative Phase 8 adapter exists.
        if (!HasPaymentEvidence) return 0;
        var count = 0;
        foreach (var id in await store.DueAsync(clock.GetUtcNow(), options.Value.BatchSize, ct))
        {
            await unit.ExecuteAsync(async token =>
            {
                var candidate = await store.GetAsync(id, token) ?? throw new InvalidOperationException("Scheduled refund missing.");
                await unit.LockAsync(candidate.PaymentId, token);
                // The store reloads tracked rows after obtaining the lock, avoiding an obsolete state read.
                var refund = await store.GetAsync(id, token) ?? throw new InvalidOperationException("Scheduled refund missing.");
                if (!refund.IsEligible(clock.GetUtcNow())) return;
                refund.Schedule(clock.GetUtcNow()); await store.SaveAsync(token);
                var eventId = RefundAggregate.StableId($"refund-due-{refund.AttemptCount}", refund.PaymentId);
                await outbox.EnqueueAsync(new RefundDueIntegrationEventV1(eventId, clock.GetUtcNow(), refund.Id, refund.AttemptCount), refund.CorrelationId, token);
                count++;
            }, ct);
        }
        return count;
    }
}

public sealed class RefundCompletion(IRefundStore store, IRefundUnitOfWork unit, IWalletOperations wallet,
    IEnumerable<IRefundPaymentEvidence> evidence, IOutboxWriter outbox, IOptions<RefundProcessingOptions> options, TimeProvider clock)
{
    public Task ProcessAsync(Guid refundId, int attempt, CancellationToken ct) => unit.ExecuteAsync(async token =>
    {
        var candidate = await store.GetAsync(refundId, token) ?? throw new InvalidMessageException();
        await unit.LockAsync(candidate.PaymentId, token);
        var refund = await store.GetAsync(refundId, token) ?? throw new InvalidMessageException();
        if (refund.Status == RefundStatus.Completed) return;
        if (attempt < refund.AttemptCount) return; // An obsolete retry delivery cannot start another credit.
        if (refund.Status != RefundStatus.Processing || attempt != refund.AttemptCount || clock.GetUtcNow() < refund.DueAtUtc)
            throw new InvalidMessageException();
        var reader = evidence.SingleOrDefault() ?? throw new RefundDependencyUnavailableException();
        VerifiedRefundPayment? proof;
        try { proof = await reader.GetSucceededPaymentAsync(refund.PaymentId, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { await FailAsync(refund, "PaymentEvidenceUnavailable", token); return; }
        if (proof is null) { await FailAsync(refund, "PaymentEvidenceUnavailable", token); return; }
        if (proof.PaymentId != refund.PaymentId || proof.OrderId != refund.OrderId || proof.UserId != refund.UserId ||
            proof.Amount != refund.Amount || proof.Currency != refund.Currency)
        { await FailAsync(refund, "PaymentEvidenceMismatch", token); return; }
        WalletTransactionDto ledger;
        try
        {
            // Wallet retains its own atomic transaction. Stable input recovers a crash after wallet commit.
            ledger = await wallet.CreditAsync(refund.UserId, refund.UserId, new WalletOperation { IdempotencyKey = refund.IdempotencyKey,
                Amount = refund.Amount, Currency = refund.Currency, ReferenceType = "InventoryRefund", ReferenceId = refund.PaymentId.ToString("N"),
                Description = "Refund for unavailable inventory" }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { await FailAsync(refund, "WalletCreditFailed", token); return; }
        if (ledger.IdempotencyKey != refund.IdempotencyKey || ledger.Amount != refund.Amount || ledger.Currency != refund.Currency || ledger.Type != "Credit" ||
            ledger.Status != "Posted" || ledger.ReferenceType != "InventoryRefund" || ledger.ReferenceId != refund.PaymentId.ToString("N"))
            throw new InvalidOperationException("Wallet refund result did not match the posting request.");
        refund.Complete(ledger.Id, clock.GetUtcNow()); await store.SaveAsync(token);
        await outbox.EnqueueAsync(new RefundCompletedIntegrationEventV1(RefundAggregate.StableId("refund-completed", refund.PaymentId),
            refund.ProcessedAtUtc!.Value, refund.Id, refund.OrderId, refund.PaymentId, ledger.Id), refund.CorrelationId, token);
    }, ct);
    private async Task FailAsync(RefundAggregate refund, string code, CancellationToken ct)
    {
        refund.Retry(clock.GetUtcNow(), options.Value.Backoff(refund.AttemptCount), options.Value.MaximumAttempts, code);
        await store.SaveAsync(ct);
        if (refund.Status == RefundStatus.Failed)
            await outbox.EnqueueAsync(new RefundFailedIntegrationEventV1(RefundAggregate.StableId("refund-failed", refund.PaymentId),
                clock.GetUtcNow(), refund.Id, code), refund.CorrelationId, ct);
    }
}
