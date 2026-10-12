using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MyOnlineShop.Identity.Application;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Domain;

namespace MyOnlineShop.Identity.Infrastructure.Security;

public sealed class DefaultAdministratorOptions
{
    public bool Enabled { get; set; }
    public string? Password { get; set; }
}

public sealed class DefaultAdministratorBootstrap(
    IOptions<DefaultAdministratorOptions> options, IIdentityStore store, UserManager<User> manager,
    TimeProvider clock) : IDefaultAdministratorBootstrap
{
    public const string UserName = "zohrehkrm";
    public const string Email = "zohrehkarimi1372@gmail.com";
    public const string Phone = "+989370747630";
    public bool Enabled => options.Value.Enabled;

    public async Task ValidateConfigurationAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(options.Value.Password))
            throw new IdentityBootstrapException("IdentityBootstrap:Password is required when IdentityBootstrap:Enabled is true. Supply it through User Secrets or an environment variable.");
        var candidate = NewUser();
        foreach (var validator in manager.PasswordValidators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!(await validator.ValidateAsync(manager, candidate, options.Value.Password)).Succeeded)
                throw new IdentityBootstrapException("IdentityBootstrap:Password fails the configured Identity password policy. Use 8–128 characters with upper/lowercase letters, a digit and symbol, and no whitespace.");
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        await ValidateConfigurationAsync(cancellationToken);
        await store.InTransactionAsync(async ct =>
        {
            await store.AcquireBootstrapLockAsync(ct);
            var role = (await store.GetRolesAsync(ct)).SingleOrDefault(item => item.Id == IdentityPermissions.AdministratorRoleId);
            if (role?.Name != IdentityPermissions.AdministratorRole ||
                !role.Permissions.Contains(IdentityPermissions.ManageUsers) || !role.Permissions.Contains(IdentityPermissions.ManageRoles))
                throw new IdentityBootstrapException("The existing Administrator role and core permissions must be provisioned by Identity migrations before bootstrap.");

            var byName = await store.FindUserByNameAsync(UserName.ToUpperInvariant(), ct);
            var byEmail = await store.FindUserByEmailAsync(User.NormalizeEmail(Email), ct);
            if (byName is not null || byEmail is not null)
            {
                if (byName is null || byEmail is null || byName.Id != byEmail.Id ||
                    byName.NormalizedEmail != User.NormalizeEmail(Email) || byName.PhoneNumber != Phone ||
                    byName.Status != UserStatus.Active || string.IsNullOrWhiteSpace(byName.PasswordHash))
                    throw new IdentityBootstrapException("Default administrator username/email conflicts with an existing account or its profile/status. No account was modified.");
                var existing = await store.LockUserAsync(byName.Id, ct)
                    ?? throw new IdentityBootstrapException("Default administrator changed during bootstrap; retry startup.");
                if (existing.UserName != UserName || existing.NormalizedEmail != User.NormalizeEmail(Email) ||
                    existing.PhoneNumber != Phone || existing.Status != UserStatus.Active)
                    throw new IdentityBootstrapException("Default administrator profile/status changed during bootstrap. No account was modified.");
                // Never replace an existing password, even if the configured bootstrap password changes.
                if (!(await store.GetUserRolesAsync(byName.Id, ct)).Contains(IdentityPermissions.AdministratorRole))
                {
                    await store.SetUserRoleAsync(byName.Id, IdentityPermissions.AdministratorRoleId, true, ct);
                    existing.InvalidateAccessTokens();
                    Audit(byName.Id, "DefaultAdministratorRoleAssigned");
                }
                return true;
            }
            if (await store.PhoneExistsAsync(Phone, ct))
                throw new IdentityBootstrapException("Default administrator phone conflicts with an existing account. No account was modified.");

            var user = NewUser();
            ct.ThrowIfCancellationRequested();
            var created = await manager.CreateAsync(user, options.Value.Password!);
            if (!created.Succeeded)
                throw new IdentityBootstrapException("Default administrator creation failed Identity validation. No administrator was created.");
            ct.ThrowIfCancellationRequested();
            await store.SetUserRoleAsync(user.Id, IdentityPermissions.AdministratorRoleId, true, ct);
            Audit(user.Id, "DefaultAdministratorCreated");
            return true;
        }, cancellationToken);
    }

    private User NewUser()
    {
        var user = User.Create(Email, "Zohreh", "Karimi", Phone, clock.GetUtcNow());
        user.SetUserName(UserName);
        return user;
    }

    private void Audit(Guid userId, string action) => store.AddAudit(IdentityAudit.Create(
        null, userId, action, "Succeeded", IdentityPermissions.AdministratorRoleId.ToString(), "startup-bootstrap", clock.GetUtcNow()));
}
