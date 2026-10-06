using System.ComponentModel.DataAnnotations;

namespace MyOnlineShop.Identity.Contracts;

public static class IdentityPermissions
{
    public const string ManageUsers = "identity.users.manage";
    public const string ManageRoles = "identity.roles.manage";
    public const string ManageCatalog = "catalog.manage";
    public const string ManagePricing = "pricing.manage";
    public const string ManageDiscount = "discount.manage";
    public const string ViewOrders = "orders.view";
    public const string ManageOrders = "orders.manage";
    public const string CreditWallet = "wallet.credit";
    public const string ViewInventory = "inventory.view";
    public const string ReceiveInventory = "inventory.receive";
    public const string AdjustInventory = "inventory.adjust";
    public const string DeductInventory = "inventory.deduct";
    public const string AdministratorRole = "Administrator";
    public const string CustomerRole = "Customer";
    public static readonly Guid AdministratorRoleId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid CustomerRoleId = Guid.Parse("00000000-0000-0000-0000-000000000002");
}

public sealed class RegisterCommand
{
    [Required, StringLength(100)] public string FirstName { get; init; } = string.Empty;
    [Required, StringLength(100)] public string LastName { get; init; } = string.Empty;
    [Required, EmailAddress, StringLength(254)] public string Email { get; init; } = string.Empty;
    [Required, StringLength(128, MinimumLength = 12)] public string Password { get; init; } = string.Empty;
    [RegularExpression(@"^\+[1-9][0-9]{7,14}$")] public string? PhoneNumber { get; init; }
}

public sealed class LoginCommand
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; init; } = string.Empty;
    [Required, StringLength(128)] public string Password { get; init; } = string.Empty;
}

public sealed class RefreshCommand
{
    [Required, StringLength(64, MinimumLength = 64)] public string RefreshToken { get; init; } = string.Empty;
}

public sealed class CreateRoleCommand
{
    [Required, RegularExpression(@"^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    public string Name { get; init; } = string.Empty;
}

public sealed class ChangeStatusCommand
{
    [Required] public bool? IsActive { get; init; }
}

public sealed class TokenPair
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public DateTimeOffset AccessTokenExpiresAtUtc { get; init; }
    public DateTimeOffset RefreshTokenExpiresAtUtc { get; init; }
    public string TokenType { get; init; } = "Bearer";
}

public sealed record UserDto(Guid Id, string FirstName, string LastName, string Email,
    string? PhoneNumber, bool IsActive, IReadOnlyList<string> Roles);
public sealed record RoleDto(Guid Id, string Name, IReadOnlyList<string> Permissions);

public interface IIdentityCommands
{
    Task<UserDto> RegisterAsync(RegisterCommand command, CancellationToken cancellationToken);
    Task<TokenPair> LoginAsync(LoginCommand command, CancellationToken cancellationToken);
    Task<TokenPair> RefreshAsync(RefreshCommand command, CancellationToken cancellationToken);
    Task LogoutAsync(RefreshCommand command, CancellationToken cancellationToken);
}

public interface IIdentityAdministration
{
    Task<RoleDto> CreateRoleAsync(Guid actorId, CreateRoleCommand command, CancellationToken cancellationToken);
    Task SetUserRoleAsync(Guid actorId, Guid userId, Guid roleId, bool assigned, CancellationToken cancellationToken);
    Task SetRolePermissionAsync(Guid actorId, Guid roleId, string permission, bool assigned, CancellationToken cancellationToken);
    Task SetUserStatusAsync(Guid actorId, Guid userId, bool active, CancellationToken cancellationToken);
    Task<UserDto> BootstrapAdministratorAsync(RegisterCommand command, CancellationToken cancellationToken);
}

public interface IIdentityQueries
{
    Task<UserDto> GetUserAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken cancellationToken);
}
