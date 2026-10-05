using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Domain;

namespace MyOnlineShop.Identity.Application;

public sealed record SessionReference(Guid UserId, Guid SessionId);
public sealed record AuthorizationSnapshot(Guid UserId, Guid SecurityStamp,
    IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);
public sealed class RefreshSecret(string value, string hash)
{
    public string Value { get; } = value;
    public string Hash { get; } = hash;
}

public interface IIdentityStore
{
    Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
    Task<User?> FindUserByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);
    Task<User?> LockUserAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> PhoneExistsAsync(string phone, CancellationToken cancellationToken);
    Task<SessionReference?> FindSessionAsync(string tokenHash, CancellationToken cancellationToken);
    Task<RefreshSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<RefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken);
    Task RevokeUserSessionsAsync(Guid userId, DateTimeOffset now, string reason, CancellationToken cancellationToken);
    Task<AuthorizationSnapshot?> GetAuthorizationAsync(Guid userId, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetUserRolesAsync(Guid userId, CancellationToken cancellationToken);
    Task<Role?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken);
    Task<bool> HasAdministratorAsync(CancellationToken cancellationToken);
    Task AcquireBootstrapLockAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetUserPermissionsAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> RoleNameExistsAsync(string normalizedName, CancellationToken cancellationToken);
    Task SetUserRoleAsync(Guid userId, Guid roleId, bool assigned, CancellationToken cancellationToken);
    Task SetRolePermissionAsync(Guid roleId, string permission, bool assigned, CancellationToken cancellationToken);
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken cancellationToken);
    Task<User?> GetUserAsync(Guid userId, CancellationToken cancellationToken);
    void AddUser(User user);
    void AddRole(Role role);
    void AddSession(RefreshSession session);
    void AddRefreshToken(RefreshToken token);
    void AddAudit(IdentityAudit audit);
    Task SaveAsync(CancellationToken cancellationToken);
}

public interface IIdentityPasswords
{
    string Hash(User user, string password);
    bool Verify(User? user, string password, out bool needsRehash);
}

public interface IIdentityTokens
{
    TimeSpan RefreshLifetime { get; }
    int MaximumFailedLogins { get; }
    TimeSpan LockoutDuration { get; }
    RefreshSecret CreateRefreshToken();
    string HashRefreshToken(string value);
    TokenPair Issue(User user, RefreshSession session, IReadOnlyList<string> roles, RefreshSecret refresh);
}
