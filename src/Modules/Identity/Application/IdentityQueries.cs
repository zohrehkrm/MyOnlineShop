using MyOnlineShop.Identity.Contracts;

namespace MyOnlineShop.Identity.Application;

public sealed class IdentityQueries(IIdentityStore store) : IIdentityQueries
{
    public async Task<UserDto> GetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await store.GetUserAsync(userId, cancellationToken) ?? throw IdentityException.NotFound();
        return AuthenticationCommands.ToDto(user, await store.GetUserRolesAsync(userId, cancellationToken));
    }
    public Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken cancellationToken) =>
        store.GetRolesAsync(cancellationToken);
}
