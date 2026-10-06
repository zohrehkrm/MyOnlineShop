using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;
using MyOnlineShop.Inventory.Application;
using Xunit;

namespace MyOnlineShop.Messaging.Tests;

internal sealed class ConnectionProhibited : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData data, InterceptionResult result) => throw new InvalidOperationException("Offline check: connection prohibited.");
    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData data, InterceptionResult result, CancellationToken ct = default) => throw new InvalidOperationException("Offline check: connection prohibited.");
}
public sealed class ModelTests
{
    [Fact]
    public async Task Durable_envelope_and_deduplication_history_cannot_be_rewritten_or_deleted()
    {
        using var db = new MessagingDbContext(new DbContextOptionsBuilder<MessagingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var envelope = MyOnlineShop.BuildingBlocks.Abstractions.Messaging.MessageEnvelope.From(Events.Payment(new Clock(), Guid.NewGuid(), Guid.NewGuid()), "history");
        var message = new OutboxMessage { Id = envelope.MessageId, EventType = envelope.EventType, Version = envelope.Version, CreatedAtUtc = envelope.OccurredAtUtc,
            Payload = envelope.Payload.GetRawText(), Fingerprint = MessageFingerprint.Of(envelope), CorrelationId = envelope.CorrelationId };
        db.Outbox.Add(message); await db.SaveChangesAsync(); message.Payload = "rewritten";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        db.Outbox.Remove(await db.Outbox.SingleAsync()); await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        var inbox = new InboxMessage { MessageId = envelope.MessageId, ConsumerName = "test", Fingerprint = MessageFingerprint.Of(envelope), EnvelopeJson = "original", Status = "Processed" };
        db.Inbox.Add(inbox); await db.SaveChangesAsync(); inbox.Fingerprint = new string('A', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        db.Inbox.Remove(await db.Inbox.SingleAsync()); await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
    [Fact]
    public async Task Messaging_model_migration_unique_inbox_and_strict_transaction_writer_are_checked_offline()
    {
        using var db = new MessagingDbContext(new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlServer("Server=localhost;Database=MessagingOffline;Integrated Security=True", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "messaging"))
            .AddInterceptors(new ConnectionProhibited()).Options);
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Single(db.Database.GetMigrations());
        Assert.All(db.Model.GetEntityTypes(), entity => Assert.Equal("messaging", entity.GetSchema()));
        var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE [messaging].[Outbox]", script); Assert.Contains("CREATE TABLE [messaging].[Inbox]", script);
        Assert.Contains("PRIMARY KEY ([MessageId], [ConsumerName])", script); Assert.Contains("rowversion", script); Assert.DoesNotContain("DROP TABLE", script);
        Assert.Contains("[LeaseId]", script); Assert.Contains("[LeaseUntilUtc]", script); Assert.Contains("CK_Outbox_Lease", script);
        var writer = new SqlOutboxWriter(db, Options.Create(new MessagingOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.EnqueueAsync(Events.Payment(new Clock(), Guid.NewGuid(), Guid.NewGuid()), "test", default));
        Assert.DoesNotContain(typeof(PaymentSucceededConsumer).Assembly.GetReferencedAssemblies(), value => value.Name!.Contains("RabbitMQ") || value.Name.Contains("Infrastructure") || value.Name.Contains("Payment"));
        Assert.DoesNotContain(typeof(MyOnlineShop.Order.Application.CheckoutCommands).Assembly.GetReferencedAssemblies(), value => value.Name!.Contains("RabbitMQ"));
    }
}
