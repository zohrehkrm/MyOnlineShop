using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;

namespace MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = "";
    public int Version { get; set; }
    public string Payload { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public string? Error { get; set; }
    public string Status { get; set; } = "Pending";
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public MessageEnvelope Envelope() => new(Id, EventType, Version, CreatedAtUtc, CorrelationId, JsonSerializer.Deserialize<JsonElement>(Payload));
}
public sealed class InboxMessage
{
    public Guid MessageId { get; set; }
    public string ConsumerName { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string EnvelopeJson { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public int RetryCount { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
}
public sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();
    private void GuardHistory()
    {
        foreach (var entry in ChangeTracker.Entries<OutboxMessage>())
            if (entry.State == EntityState.Deleted || entry.State == EntityState.Modified && entry.Properties.Any(property => property.IsModified &&
                property.Metadata.Name is not ("Status" or "RetryCount" or "LastAttemptAtUtc" or "NextAttemptAtUtc" or "ProcessedAtUtc" or "Error" or "LeaseId" or "LeaseUntilUtc" or "RowVersion")))
                throw new InvalidOperationException("Outbox envelopes cannot be rewritten or silently deleted.");
        foreach (var entry in ChangeTracker.Entries<InboxMessage>())
            if (entry.State == EntityState.Deleted || entry.State == EntityState.Modified && entry.Properties.Any(property => property.IsModified &&
                property.Metadata.Name is not ("Status" or "RetryCount" or "LastAttemptAtUtc" or "NextAttemptAtUtc" or "ProcessedAtUtc" or "Error")))
                throw new InvalidOperationException("Inbox identities and envelopes cannot be rewritten or silently deleted.");
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess) { GuardHistory(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    { GuardHistory(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct); }
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("messaging");
        var outbox = model.Entity<OutboxMessage>();
        outbox.ToTable("Outbox", table =>
        {
            table.HasCheckConstraint("CK_Outbox_State", "[Status] IN ('Pending','Publishing','Published','Dead') AND [RetryCount] >= 0");
            table.HasCheckConstraint("CK_Outbox_Lease", "([Status] = 'Publishing' AND [LeaseId] IS NOT NULL AND [LeaseUntilUtc] IS NOT NULL) OR ([Status] <> 'Publishing' AND [LeaseId] IS NULL AND [LeaseUntilUtc] IS NULL)");
            table.HasCheckConstraint("CK_Outbox_Processed", "([Status] = 'Published' AND [ProcessedAtUtc] IS NOT NULL) OR ([Status] <> 'Published' AND [ProcessedAtUtc] IS NULL)");
        });
        outbox.HasKey(value => value.Id); outbox.Property(value => value.Id).ValueGeneratedNever();
        outbox.HasIndex(value => new { value.Status, value.NextAttemptAtUtc, value.LeaseUntilUtc });
        outbox.Property(value => value.EventType).HasMaxLength(128).IsRequired();
        outbox.Property(value => value.Payload).IsRequired(); outbox.Property(value => value.Fingerprint).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        outbox.Property(value => value.CorrelationId).HasMaxLength(128); outbox.Property(value => value.Error).HasMaxLength(200);
        outbox.Property(value => value.Status).HasMaxLength(16); outbox.Property(value => value.RowVersion).IsRowVersion();
        var inbox = model.Entity<InboxMessage>();
        inbox.ToTable("Inbox", table => table.HasCheckConstraint("CK_Inbox_State", "[Status] IN ('Pending','Processed','Rejected','Dead') AND [RetryCount] >= 0"));
        inbox.HasKey(value => new { value.MessageId, value.ConsumerName });
        inbox.Property(value => value.MessageId).ValueGeneratedNever(); inbox.Property(value => value.ConsumerName).HasMaxLength(128);
        inbox.Property(value => value.Fingerprint).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        inbox.Property(value => value.EnvelopeJson).IsRequired(); inbox.Property(value => value.Status).HasMaxLength(16); inbox.Property(value => value.Error).HasMaxLength(200);
        inbox.HasIndex(value => new { value.Status, value.NextAttemptAtUtc });
    }
}
