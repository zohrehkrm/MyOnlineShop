namespace MyOnlineShop.Identity.Domain;

public sealed class IdentityAudit
{
    private IdentityAudit() { }
    public Guid Id { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public Guid? TargetUserId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string Outcome { get; private set; } = string.Empty;
    public string? TargetReference { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public static IdentityAudit Create(Guid? actor, Guid? target, string action, string outcome,
        string? reference, string correlationId, DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(), ActorUserId = actor, TargetUserId = target, Action = action,
            Outcome = outcome, TargetReference = reference, CorrelationId = correlationId, OccurredAtUtc = now
        };
}
