namespace MyOnlineShop.Identity.Domain;

public sealed class Role
{
    private Role() { }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty;
    public static Role Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64)
            throw new ArgumentException("Role name is invalid.");
        return new Role { Id = Guid.NewGuid(), Name = name.Trim(), NormalizedName = name.Trim().ToUpperInvariant() };
    }
}

public sealed class Permission
{
    public string Key { get; private set; } = string.Empty;
}

public sealed class UserRole
{
    private UserRole() { }
    public UserRole(Guid userId, Guid roleId) { UserId = userId; RoleId = roleId; }
    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }
}

public sealed class RolePermission
{
    private RolePermission() { }
    public RolePermission(Guid roleId, string permissionKey) { RoleId = roleId; PermissionKey = permissionKey; }
    public Guid RoleId { get; private set; }
    public string PermissionKey { get; private set; } = string.Empty;
}
