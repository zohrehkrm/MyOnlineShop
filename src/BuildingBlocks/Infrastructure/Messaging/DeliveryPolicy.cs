using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;

namespace MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;

public static class DeliveryPolicy
{
    public static string FailureStatus(int attempts, bool retryable, MessagingOptions options) => retryable && attempts < options.MaxAttempts ? "Pending" : "Dead";
    public static ConsumptionDisposition? InboxDisposition(InboxMessage? entry, MessageEnvelope envelope, DateTimeOffset now)
    {
        if (entry is null) return null;
        if (entry.Fingerprint != MessageFingerprint.Of(envelope)) throw new InvalidMessageException();
        if (entry.Status is "Processed" or "Rejected") return ConsumptionDisposition.Duplicate;
        if (entry.Status == "Dead") return ConsumptionDisposition.Dead;
        return entry.NextAttemptAtUtc > now ? ConsumptionDisposition.Deferred : null;
    }
}
public interface IMessageSettlement
{
    Task AcknowledgeAsync(CancellationToken ct);
    Task RejectAsync(bool requeue, CancellationToken ct);
}
public sealed class ConsumerDeliveryDispatcher(IMessageProcessor processor, MessagingOptions options)
{
    public async Task DeliverAsync(IMessageConsumer consumer, MessageEnvelope envelope, IMessageSettlement settlement, CancellationToken ct)
    {
        ConsumptionDisposition result;
        try { result = await processor.ProcessAsync(consumer, envelope, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidMessageException) { result = ConsumptionDisposition.Dead; }
        // Infrastructure failure leaves the original broker delivery unacked until requeued.
        catch { result = ConsumptionDisposition.Retry; }
        if (result is ConsumptionDisposition.Processed or ConsumptionDisposition.Duplicate or ConsumptionDisposition.Rejected)
            await settlement.AcknowledgeAsync(ct);
        else
        {
            var requeue = result != ConsumptionDisposition.Dead;
            if (requeue)
            { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.RetrySeconds)); await timer.WaitForNextTickAsync(ct); }
            await settlement.RejectAsync(requeue, ct);
        }
    }
}
