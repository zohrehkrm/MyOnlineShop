using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;

namespace MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;

public sealed class SqlMessageProcessor(MessagingDbContext db, IEnumerable<ILocalSqlTransactionParticipant> participants,
    IOutboxWriter outbox, IOptions<MessagingOptions> options, TimeProvider clock, ILogger<SqlMessageProcessor> logger) : IMessageProcessor
{
    public async Task<ConsumptionDisposition> ProcessAsync(IMessageConsumer consumer, MessageEnvelope envelope, CancellationToken ct)
    {
        envelope.Validate();
        if (consumer.ConsumerName.Length is < 1 or > 128 || envelope.EventType != consumer.EventType || envelope.Version != consumer.Version ||
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(envelope)) > options.Value.MaximumPayloadBytes) throw new InvalidMessageException();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockKey = "Inbox:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(consumer.ConsumerName))) + ":" + envelope.MessageId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource={lockKey}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
                IF @result < 0 THROW 51010, 'Inbox processing is busy.', 1;
                """, ct);
            var hash = MessageFingerprint.Of(envelope); var now = clock.GetUtcNow();
            var entry = await db.Inbox.SingleOrDefaultAsync(value => value.MessageId == envelope.MessageId && value.ConsumerName == consumer.ConsumerName, ct);
            if (DeliveryPolicy.InboxDisposition(entry, envelope, now) is { } disposition) return disposition;
            entry ??= new() { MessageId = envelope.MessageId, ConsumerName = consumer.ConsumerName, Fingerprint = hash,
                EnvelopeJson = JsonSerializer.Serialize(envelope), NextAttemptAtUtc = now };
            var enlisted = new List<ILocalSqlTransactionParticipant>();
            try
            {
                foreach (var name in consumer.TransactionParticipants.Distinct(StringComparer.Ordinal))
                {
                    if (name == "messaging") throw new InvalidOperationException("Messaging is already the transaction owner.");
                    var participant = participants.Single(value => value.Name == name); enlisted.Add(participant);
                    await participant.EnlistAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), ct);
                }
                await transaction.CreateSavepointAsync("consumer_work", ct);
                IIntegrationEvent? rejection;
                try { rejection = await consumer.HandleAsync(envelope, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    await transaction.RollbackToSavepointAsync("consumer_work", ct); db.ChangeTracker.Clear();
                    entry.RetryCount++; entry.LastAttemptAtUtc = now;
                    var invalid = error is InvalidMessageException or JsonException;
                    entry.Status = invalid || entry.RetryCount >= options.Value.MaxAttempts ? "Dead" : "Pending";
                    entry.Error = invalid ? "Invalid message contract." : "Consumer processing failed.";
                    entry.NextAttemptAtUtc = now.Add(options.Value.Backoff(entry.RetryCount));
                    await PersistAsync(entry, ct); await transaction.CommitAsync(ct);
                    logger.LogWarning("Inbox failed {MessageId} {ConsumerName} {RetryCount} {FailureKind} {CorrelationId}", envelope.MessageId, consumer.ConsumerName,
                        entry.RetryCount, entry.Status, envelope.CorrelationId);
                    return entry.Status == "Dead" ? ConsumptionDisposition.Dead : ConsumptionDisposition.Retry;
                }
                if (rejection is not null)
                {
                    await transaction.RollbackToSavepointAsync("consumer_work", ct); db.ChangeTracker.Clear();
                    await outbox.EnqueueAsync(rejection, envelope.CorrelationId, ct);
                }
                entry.Status = rejection is null ? "Processed" : "Rejected"; entry.ProcessedAtUtc = now; entry.LastAttemptAtUtc = now; entry.Error = null;
                await PersistAsync(entry, ct); await transaction.CommitAsync(ct);
                logger.LogInformation("Inbox completed {MessageId} {ConsumerName} {Outcome} {CorrelationId}", envelope.MessageId, consumer.ConsumerName, entry.Status, envelope.CorrelationId);
                return rejection is null ? ConsumptionDisposition.Processed : ConsumptionDisposition.Rejected;
            }
            finally { foreach (var participant in enlisted.AsEnumerable().Reverse()) await participant.DetachAsync(CancellationToken.None); }
        });
    }
    private async Task PersistAsync(InboxMessage entry, CancellationToken ct)
    {
        var exists = await db.Inbox.AnyAsync(value => value.MessageId == entry.MessageId && value.ConsumerName == entry.ConsumerName, ct);
        if (exists)
        {
            var state = db.Entry(entry);
            state.State = EntityState.Unchanged;
            foreach (var property in new[] { "Status", "RetryCount", "LastAttemptAtUtc", "NextAttemptAtUtc", "ProcessedAtUtc", "Error" }) state.Property(property).IsModified = true;
        }
        else db.Inbox.Add(entry);
        await db.SaveChangesAsync(ct);
    }
}
