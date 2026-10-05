using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Domain;

namespace MyOnlineShop.Identity.Application;

public sealed class AdministrationCommands(IIdentityStore store, IIdentityPasswords passwords,
    TimeProvider clock, IRequestContext request) : IIdentityAdministration
{
    public async Task<RoleDto> CreateRoleAsync(Guid actorId, CreateRoleCommand command, CancellationToken cancellationToken)
    {
        IdentityValidation.Validate(command);
        return await store.InTransactionAsync(async ct =>
        {
            await AuthorizeAsync(actorId, IdentityPermissions.ManageRoles, ct);
            var role = Role.Create(command.Name);
            if (await store.RoleNameExistsAsync(role.NormalizedName, ct))
                throw new IdentityException("role_conflict", 409, "A role with this name already exists.");
            store.AddRole(role);
            Audit(actorId, null, "RoleCreated", role.Id.ToString());
            return new RoleDto(role.Id, role.Name, []);
        }, cancellationToken);
    }

    public async Task SetUserRoleAsync(Guid actorId, Guid userId, Guid roleId, bool assigned, CancellationToken cancellationToken)
    {
        await store.InTransactionAsync(async ct =>
        {
            await AuthorizeAsync(actorId, IdentityPermissions.ManageUsers, ct);
            var user = await store.LockUserAsync(userId, ct) ?? throw IdentityException.NotFound();
            if (await store.GetRoleAsync(roleId, ct) is null) throw IdentityException.NotFound();
            await store.SetUserRoleAsync(userId, roleId, assigned, ct);
            user.InvalidateAccessTokens();
            Audit(actorId, userId, assigned ? "RoleAssigned" : "RoleRemoved", roleId.ToString());
            return true;
        }, cancellationToken);
    }

    public async Task SetRolePermissionAsync(Guid actorId, Guid roleId, string permission, bool assigned, CancellationToken cancellationToken)
    {
        if (permission is not (IdentityPermissions.ManageUsers or IdentityPermissions.ManageRoles or IdentityPermissions.ManageCatalog or
            IdentityPermissions.ViewInventory or IdentityPermissions.ReceiveInventory or IdentityPermissions.AdjustInventory or IdentityPermissions.DeductInventory or
            IdentityPermissions.ManagePricing or IdentityPermissions.ManageDiscount))
            throw new IdentityException("validation_error", 400, "Permission is not part of the supported permission catalog.");
        await store.InTransactionAsync(async ct =>
        {
            await AuthorizeAsync(actorId, IdentityPermissions.ManageRoles, ct);
            if (await store.GetRoleAsync(roleId, ct) is null) throw IdentityException.NotFound();
            await store.SetRolePermissionAsync(roleId, permission, assigned, ct);
            Audit(actorId, null, assigned ? "PermissionAssigned" : "PermissionRemoved", $"{roleId}:{permission}");
            return true;
        }, cancellationToken);
    }

    public async Task SetUserStatusAsync(Guid actorId, Guid userId, bool active, CancellationToken cancellationToken)
    {
        await store.InTransactionAsync(async ct =>
        {
            await AuthorizeAsync(actorId, IdentityPermissions.ManageUsers, ct);
            var user = await store.LockUserAsync(userId, ct) ?? throw IdentityException.NotFound();
            user.ChangeStatus(active ? UserStatus.Active : UserStatus.Inactive);
            await store.RevokeUserSessionsAsync(userId, clock.GetUtcNow(), "StatusChanged", ct);
            Audit(actorId, userId, active ? "UserActivated" : "UserDeactivated", null);
            return true;
        }, cancellationToken);
    }

    public async Task<UserDto> BootstrapAdministratorAsync(RegisterCommand command, CancellationToken cancellationToken)
    {
        IdentityValidation.Validate(command);
        return await store.InTransactionAsync(async ct =>
        {
            await store.AcquireBootstrapLockAsync(ct);
            if (await store.HasAdministratorAsync(ct))
                throw new IdentityException("bootstrap_disabled", 409, "An administrator already exists.");
            if (await store.FindUserByEmailAsync(User.NormalizeEmail(command.Email), ct) is not null ||
                (command.PhoneNumber is not null && await store.PhoneExistsAsync(command.PhoneNumber, ct)))
                throw new IdentityException("registration_conflict", 409, "Administrator details conflict with an existing user.");
            var user = User.Create(command.Email, command.FirstName, command.LastName, command.PhoneNumber, clock.GetUtcNow());
            user.SetPasswordHash(passwords.Hash(user, command.Password));
            store.AddUser(user);
            await store.SaveAsync(ct);
            await store.SetUserRoleAsync(user.Id, IdentityPermissions.AdministratorRoleId, true, ct);
            Audit(null, user.Id, "AdministratorBootstrapped", IdentityPermissions.AdministratorRoleId.ToString());
            return AuthenticationCommands.ToDto(user, [IdentityPermissions.AdministratorRole]);
        }, cancellationToken);
    }

    private async Task AuthorizeAsync(Guid actorId, string permission, CancellationToken cancellationToken)
    {
        var actor = await store.GetUserAsync(actorId, cancellationToken);
        if (actor?.Status != UserStatus.Active ||
            !(await store.GetUserPermissionsAsync(actorId, cancellationToken)).Contains(permission))
            throw new IdentityException("forbidden", 403, "This action is not permitted.");
    }
    private void Audit(Guid? actor, Guid? target, string action, string? reference) =>
        store.AddAudit(IdentityAudit.Create(actor, target, action, "Succeeded", reference, request.CorrelationId, clock.GetUtcNow()));
}
