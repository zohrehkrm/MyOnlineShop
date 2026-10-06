using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;

namespace MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;

public static class MessageFingerprint
{
    public static string Of(MessageEnvelope envelope) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope)));
}
public sealed class SqlOutboxWriter(MessagingDbContext db, IOptions<MessagingOptions> options) : IOutboxWriter
{
    public async Task EnqueueAsync(IIntegrationEvent message, string correlationId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Outbox requires enlistment in the business SQL transaction.");
        var envelope = MessageEnvelope.From(message, correlationId); var payload = envelope.Payload.GetRawText();
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(envelope)) > options.Value.MaximumPayloadBytes) throw new InvalidMessageException();
        var hash = MessageFingerprint.Of(envelope);
        var existing = await db.Outbox.SingleOrDefaultAsync(value => value.Id == envelope.MessageId, ct);
        if (existing is not null)
        { if (existing.Fingerprint != hash) throw new InvalidMessageException(); return; }
        db.Outbox.Add(new() { Id = envelope.MessageId, EventType = envelope.EventType, Version = envelope.Version, Payload = payload,
            Fingerprint = hash, CorrelationId = envelope.CorrelationId, CreatedAtUtc = envelope.OccurredAtUtc, NextAttemptAtUtc = envelope.OccurredAtUtc });
        await db.SaveChangesAsync(ct);
    }
}
public interface IOutboxDeliveryStore
{
    Task<IReadOnlyList<OutboxMessage>> ClaimAsync(CancellationToken ct);
    Task<bool> RenewAsync(OutboxMessage message, CancellationToken ct);
    Task CompleteAsync(OutboxMessage message, CancellationToken ct);
    Task FailAsync(OutboxMessage message, bool retryable, string safeError, CancellationToken ct);
}
public sealed class SqlOutboxDeliveryStore(MessagingDbContext db, IOptions<MessagingOptions> options, TimeProvider clock) : IOutboxDeliveryStore
{
    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(CancellationToken ct)
    {
        var settings = options.Value; var now = clock.GetUtcNow(); var lease = Guid.NewGuid(); var until = now.AddSeconds(settings.LeaseSeconds);
        // One atomic claim. READCOMMITTEDLOCK supports deployments using READ_COMMITTED_SNAPSHOT.
        return await db.Outbox.FromSqlInterpolated($"""
            ;WITH candidates AS (
                SELECT TOP ({settings.BatchSize}) * FROM [messaging].[Outbox] WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                WHERE ([Status] = 'Pending' AND [NextAttemptAtUtc] <= {now}) OR ([Status] = 'Publishing' AND [LeaseUntilUtc] <= {now})
                ORDER BY [CreatedAtUtc], [Id]
            )
            UPDATE candidates SET
                [Status] = CASE WHEN [RetryCount] >= {settings.MaxAttempts} THEN 'Dead' ELSE 'Publishing' END,
                [LeaseId] = CASE WHEN [RetryCount] >= {settings.MaxAttempts} THEN NULL ELSE {lease} END,
                [LeaseUntilUtc] = CASE WHEN [RetryCount] >= {settings.MaxAttempts} THEN NULL ELSE {until} END,
                [Error] = CASE WHEN [RetryCount] >= {settings.MaxAttempts} THEN 'Attempt limit exhausted; delivery outcome may be unknown.' ELSE [Error] END,
                [LastAttemptAtUtc] = {now},
                [RetryCount] = CASE WHEN [RetryCount] >= {settings.MaxAttempts} THEN [RetryCount] ELSE [RetryCount] + 1 END
            OUTPUT INSERTED.*;
            """).AsNoTracking().ToListAsync(ct);
    }
    public async Task CompleteAsync(OutboxMessage message, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [messaging].[Outbox] SET [Status] = 'Published', [ProcessedAtUtc] = {now}, [Error] = NULL, [LeaseId] = NULL, [LeaseUntilUtc] = NULL
            WHERE [Id] = {message.Id} AND [LeaseId] = {message.LeaseId} AND [Status] = 'Publishing';
            """, ct);
    }
    public async Task<bool> RenewAsync(OutboxMessage message, CancellationToken ct)
    {
        var until = clock.GetUtcNow().AddSeconds(options.Value.LeaseSeconds);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [messaging].[Outbox] SET [LeaseUntilUtc] = {until}
            WHERE [Id] = {message.Id} AND [LeaseId] = {message.LeaseId} AND [Status] = 'Publishing';
            """, ct) == 1;
    }
    public async Task FailAsync(OutboxMessage message, bool retryable, string safeError, CancellationToken ct)
    {
        var status = DeliveryPolicy.FailureStatus(message.RetryCount, retryable, options.Value);
        var next = clock.GetUtcNow().Add(options.Value.Backoff(message.RetryCount));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [messaging].[Outbox] SET [Status] = {status}, [NextAttemptAtUtc] = {next}, [Error] = {safeError}, [LeaseId] = NULL, [LeaseUntilUtc] = NULL
            WHERE [Id] = {message.Id} AND [LeaseId] = {message.LeaseId} AND [Status] = 'Publishing';
            """, ct);
    }
}
public sealed class OutboxDispatcher(IOutboxDeliveryStore store, IMessagePublisher publisher, IOptions<MessagingOptions> options,
    ILogger<OutboxDispatcher> logger)
{
    public async Task PublishBatchAsync(CancellationToken ct)
    {
        foreach (var message in await store.ClaimAsync(ct))
        {
            if (message.Status != "Publishing") continue;
            try
            {
                if (!await store.RenewAsync(message, ct)) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.PublishTimeoutSeconds));
                var envelope = message.Envelope(); envelope.Validate(); await publisher.PublishAsync(envelope, timeout.Token);
                await store.CompleteAsync(message, ct);
                logger.LogInformation("Outbox published {MessageId} {EventType} {RetryCount} {CorrelationId}", message.Id, message.EventType, message.RetryCount, message.CorrelationId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // Never retain exception messages: broker/SQL exceptions can contain credentials and connection strings.
                var permanent = error is InvalidMessageException or JsonException;
                var safe = permanent ? "Invalid event envelope." : "Publish or completion failed; delivery outcome may be unknown.";
                await store.FailAsync(message, !permanent, safe, ct);
                logger.LogWarning("Outbox failed {MessageId} {EventType} {RetryCount} {FailureKind} {CorrelationId}", message.Id, message.EventType, message.RetryCount, permanent ? "InvalidEnvelope" : "Infrastructure", message.CorrelationId);
            }
        }
    }
}
public sealed class OutboxWorker(IServiceScopeFactory scopes, IOptions<MessagingOptions> options, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var unavailable = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollingSeconds));
        do
        {
            try
            {
                using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().PublishBatchAsync(stoppingToken);
                if (unavailable) logger.LogInformation("Outbox database connection recovered."); unavailable = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { if (!unavailable) logger.LogWarning("Outbox batch unavailable; durable messages remain recoverable."); unavailable = true; }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
