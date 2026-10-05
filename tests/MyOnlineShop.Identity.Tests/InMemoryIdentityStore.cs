using MyOnlineShop.Identity.Application;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Domain;

namespace MyOnlineShop.Identity.Tests;

// A test double for application/HTTP behavior. It does not verify SQL Server locking or constraints.
public sealed class InMemoryIdentityStore : IIdentityStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public Dictionary<Guid, User> Users { get; } = [];
    public Dictionary<Guid, Role> Roles { get; } = [];
    public Dictionary<Guid, RefreshSession> Sessions { get; } = [];
    public List<RefreshToken> RefreshTokens { get; } = [];
    public HashSet<(Guid User, Guid Role)> UserRoles { get; } = [];
    public HashSet<(Guid Role, string Permission)> RolePermissions { get; } = [];
    public List<IdentityAudit> Audits { get; } = [];

    public InMemoryIdentityStore()
    {
        SeedRole(IdentityPermissions.AdministratorRoleId, IdentityPermissions.AdministratorRole);
        SeedRole(IdentityPermissions.CustomerRoleId, IdentityPermissions.CustomerRole);
        RolePermissions.Add((IdentityPermissions.AdministratorRoleId, IdentityPermissions.ManageRoles));
        RolePermissions.Add((IdentityPermissions.AdministratorRoleId, IdentityPermissions.ManageUsers));
    }
    private void SeedRole(Guid id, string name)
    {
        var role = Role.Create(name);
        typeof(Role).GetProperty(nameof(Role.Id))!.SetValue(role, id);
        Roles.Add(id, role);
    }
    public async Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await action(ct); }
        finally { _gate.Release(); }
    }
    public Task<User?> FindUserByEmailAsync(string email, CancellationToken ct) => Task.FromResult(Users.Values.SingleOrDefault(user => user.NormalizedEmail == email));
    public Task<User?> LockUserAsync(Guid id, CancellationToken ct) => GetUserAsync(id, ct);
    public Task<User?> GetUserAsync(Guid id, CancellationToken ct) => Task.FromResult(Users.GetValueOrDefault(id));
    public Task<bool> PhoneExistsAsync(string phone, CancellationToken ct) => Task.FromResult(Users.Values.Any(user => user.PhoneNumber == phone));
    public Task<SessionReference?> FindSessionAsync(string hash, CancellationToken ct)
    {
        var token = RefreshTokens.SingleOrDefault(token => token.TokenHash == hash);
        return Task.FromResult(token is null ? null : new SessionReference(Sessions[token.SessionId].UserId, token.SessionId));
    }
    public Task<RefreshSession?> GetSessionAsync(Guid id, CancellationToken ct) => Task.FromResult(Sessions.GetValueOrDefault(id));
    public Task<RefreshToken?> GetRefreshTokenAsync(string hash, CancellationToken ct) => Task.FromResult(RefreshTokens.SingleOrDefault(token => token.TokenHash == hash));
    public Task RevokeUserSessionsAsync(Guid userId, DateTimeOffset now, string reason, CancellationToken ct)
    {
        foreach (var session in Sessions.Values.Where(session => session.UserId == userId)) session.Revoke(now, reason);
        return Task.CompletedTask;
    }
    public async Task<AuthorizationSnapshot?> GetAuthorizationAsync(Guid userId, Guid sessionId, DateTimeOffset now, CancellationToken ct)
    {
        if (!Users.TryGetValue(userId, out var user) || user.Status != UserStatus.Active ||
            !Sessions.TryGetValue(sessionId, out var session) || session.UserId != userId || !session.IsActive(now)) return null;
        return new(user.Id, user.SecurityStamp, await GetUserRolesAsync(userId, ct), await GetUserPermissionsAsync(userId, ct));
    }
    public Task<IReadOnlyList<string>> GetUserRolesAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(UserRoles.Where(link => link.User == userId).Select(link => Roles[link.Role].Name).ToArray());
    public Task<IReadOnlyList<string>> GetUserPermissionsAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>((from link in UserRoles join permission in RolePermissions on link.Role equals permission.Role
            where link.User == userId select permission.Permission).Distinct().ToArray());
    public Task<Role?> GetRoleAsync(Guid id, CancellationToken ct) => Task.FromResult(Roles.GetValueOrDefault(id));
    public Task<bool> HasAdministratorAsync(CancellationToken ct) => Task.FromResult(UserRoles.Any(link => link.Role == IdentityPermissions.AdministratorRoleId));
    public Task AcquireBootstrapLockAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<bool> RoleNameExistsAsync(string name, CancellationToken ct) => Task.FromResult(Roles.Values.Any(role => role.NormalizedName == name));
    public Task SetUserRoleAsync(Guid userId, Guid roleId, bool assigned, CancellationToken ct)
    {
        if (assigned) UserRoles.Add((userId, roleId)); else UserRoles.Remove((userId, roleId));
        return Task.CompletedTask;
    }
    public Task SetRolePermissionAsync(Guid roleId, string permission, bool assigned, CancellationToken ct)
    {
        if (assigned) RolePermissions.Add((roleId, permission)); else RolePermissions.Remove((roleId, permission));
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RoleDto>>(
        Roles.Values.Select(role => new RoleDto(role.Id, role.Name, RolePermissions.Where(link => link.Role == role.Id).Select(link => link.Permission).ToArray())).ToArray());
    public void AddUser(User user) => Users.Add(user.Id, user);
    public void AddRole(Role role) => Roles.Add(role.Id, role);
    public void AddSession(RefreshSession session) => Sessions.Add(session.Id, session);
    public void AddRefreshToken(RefreshToken token) => RefreshTokens.Add(token);
    public void AddAudit(IdentityAudit audit) => Audits.Add(audit);
    public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
}
