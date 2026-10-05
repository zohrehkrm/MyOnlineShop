using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Identity.Application;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Domain;

namespace MyOnlineShop.Identity.Infrastructure.Persistence;

internal sealed class IdentityStore(IdentityDbContext context) : IIdentityStore
{
    public async Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                var result = await action(cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            });
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new IdentityException("identity_conflict", 409, "Identity details conflict with an existing record.");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new IdentityException("identity_conflict", 409, "Identity state changed. Please retry the operation.");
        }
    }

    public Task<User?> FindUserByEmailAsync(string email, CancellationToken ct) =>
        context.Users.AsNoTracking().SingleOrDefaultAsync(user => user.NormalizedEmail == email, ct);
    public Task<User?> GetUserAsync(Guid userId, CancellationToken ct) =>
        context.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId, ct);
    public Task<User?> LockUserAsync(Guid userId, CancellationToken ct) =>
        context.Users.FromSqlInterpolated($"SELECT * FROM [identity].[Users] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {userId}")
            .SingleOrDefaultAsync(ct);
    public Task<bool> PhoneExistsAsync(string phone, CancellationToken ct) =>
        context.Users.AnyAsync(user => user.PhoneNumber == phone, ct);
    public Task<SessionReference?> FindSessionAsync(string hash, CancellationToken ct) =>
        (from token in context.RefreshTokens.AsNoTracking()
         join session in context.Sessions.AsNoTracking() on token.SessionId equals session.Id
         where token.TokenHash == hash
         select new SessionReference(session.UserId, session.Id)).SingleOrDefaultAsync(ct);
    public Task<RefreshSession?> GetSessionAsync(Guid sessionId, CancellationToken ct) =>
        context.Sessions.SingleOrDefaultAsync(session => session.Id == sessionId, ct);
    public Task<RefreshToken?> GetRefreshTokenAsync(string hash, CancellationToken ct) =>
        context.RefreshTokens.SingleOrDefaultAsync(token => token.TokenHash == hash, ct);
    public async Task RevokeUserSessionsAsync(Guid userId, DateTimeOffset now, string reason, CancellationToken ct)
    {
        var sessions = await context.Sessions.Where(session => session.UserId == userId && session.RevokedAtUtc == null).ToListAsync(ct);
        foreach (var session in sessions) session.Revoke(now, reason);
    }
    public async Task<AuthorizationSnapshot?> GetAuthorizationAsync(Guid userId, Guid sessionId, DateTimeOffset now, CancellationToken ct)
    {
        var user = await (from candidate in context.Users.AsNoTracking()
            join session in context.Sessions.AsNoTracking() on candidate.Id equals session.UserId
            where candidate.Id == userId && candidate.Status == UserStatus.Active &&
                  session.Id == sessionId && session.RevokedAtUtc == null && session.ExpiresAtUtc > now
            select new { candidate.Id, candidate.SecurityStamp }).SingleOrDefaultAsync(ct);
        if (user is null) return null;
        return new AuthorizationSnapshot(user.Id, user.SecurityStamp,
            await GetUserRolesAsync(userId, ct), await GetUserPermissionsAsync(userId, ct));
    }
    public async Task<IReadOnlyList<string>> GetUserRolesAsync(Guid userId, CancellationToken ct) =>
        await (from link in context.UserRoles.AsNoTracking()
            join role in context.Roles.AsNoTracking() on link.RoleId equals role.Id
            where link.UserId == userId select role.Name).ToListAsync(ct);
    public async Task<IReadOnlyList<string>> GetUserPermissionsAsync(Guid userId, CancellationToken ct) =>
        await (from userRole in context.UserRoles.AsNoTracking()
            join permission in context.RolePermissions.AsNoTracking() on userRole.RoleId equals permission.RoleId
            where userRole.UserId == userId select permission.PermissionKey).Distinct().ToListAsync(ct);
    public Task<Role?> GetRoleAsync(Guid roleId, CancellationToken ct) =>
        context.Roles.AsNoTracking().SingleOrDefaultAsync(role => role.Id == roleId, ct);
    public Task<bool> HasAdministratorAsync(CancellationToken ct) =>
        context.UserRoles.AnyAsync(link => link.RoleId == IdentityPermissions.AdministratorRoleId, ct);
    public async Task AcquireBootstrapLockAsync(CancellationToken ct)
    {
        await context.Database.ExecuteSqlRawAsync("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'IdentityAdministratorBootstrap',
                @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 10000;
            IF @result < 0 THROW 50001, 'Identity bootstrap is busy.', 1;
            """, ct);
    }
    public Task<bool> RoleNameExistsAsync(string name, CancellationToken ct) =>
        context.Roles.AnyAsync(role => role.NormalizedName == name, ct);
    public async Task SetUserRoleAsync(Guid userId, Guid roleId, bool assigned, CancellationToken ct)
    {
        var link = await context.UserRoles.SingleOrDefaultAsync(item => item.UserId == userId && item.RoleId == roleId, ct);
        if (assigned && link is null) context.UserRoles.Add(new UserRole(userId, roleId));
        if (!assigned && link is not null) context.UserRoles.Remove(link);
    }
    public async Task SetRolePermissionAsync(Guid roleId, string permission, bool assigned, CancellationToken ct)
    {
        var link = await context.RolePermissions.SingleOrDefaultAsync(item => item.RoleId == roleId && item.PermissionKey == permission, ct);
        if (assigned && link is null) context.RolePermissions.Add(new RolePermission(roleId, permission));
        if (!assigned && link is not null) context.RolePermissions.Remove(link);
    }
    public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct)
    {
        var roles = await context.Roles.AsNoTracking().OrderBy(role => role.Name).Select(role => new { role.Id, role.Name }).ToListAsync(ct);
        var permissions = await context.RolePermissions.AsNoTracking().ToListAsync(ct);
        return roles.Select(role => new RoleDto(role.Id, role.Name,
            permissions.Where(item => item.RoleId == role.Id).Select(item => item.PermissionKey).ToArray())).ToArray();
    }
    public void AddUser(User user) => context.Users.Add(user);
    public void AddRole(Role role) => context.Roles.Add(role);
    public void AddSession(RefreshSession session) => context.Sessions.Add(session);
    public void AddRefreshToken(RefreshToken token) => context.RefreshTokens.Add(token);
    public void AddAudit(IdentityAudit audit) => context.Audit.Add(audit);
    public Task SaveAsync(CancellationToken ct) => context.SaveChangesAsync(ct);
}
