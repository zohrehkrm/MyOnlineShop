namespace MyOnlineShop.Identity.Domain;

public sealed class RefreshSession
{
    private RefreshSession() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public string? RevocationReason { get; private set; }
    public static RefreshSession Create(Guid userId, DateTimeOffset now, DateTimeOffset expires) =>
        new() { Id = Guid.NewGuid(), UserId = userId, CreatedAtUtc = now, ExpiresAtUtc = expires };
    public bool IsActive(DateTimeOffset now) => RevokedAtUtc is null && ExpiresAtUtc > now;
    public void Revoke(DateTimeOffset now, string reason)
    {
        RevokedAtUtc ??= now;
        RevocationReason ??= reason;
    }
}

public sealed class RefreshToken
{
    private RefreshToken() { }
    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UsedAtUtc { get; private set; }
    public static RefreshToken Create(Guid sessionId, string tokenHash, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), SessionId = sessionId, TokenHash = tokenHash, CreatedAtUtc = now };
    public void Consume(DateTimeOffset now)
    {
        if (UsedAtUtc is not null) throw new InvalidOperationException("Refresh token has already been used.");
        UsedAtUtc = now;
    }
}
