using Microsoft.AspNetCore.Identity;
using MyOnlineShop.Identity.Application;
using MyOnlineShop.Identity.Domain;

namespace MyOnlineShop.Identity.Infrastructure.Security;

// Identity's supported CreateAsync pipeline over the existing module-owned store.
// Updates/deletion are intentionally left to existing authorized application commands.
public sealed class BootstrapUserStore(IIdentityStore store) : IUserPasswordStore<User>
{
    public async Task<IdentityResult> CreateAsync(User user, CancellationToken cancellationToken)
    {
        store.AddUser(user);
        await store.SaveAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public Task<User?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        Guid.TryParse(userId, out var id) ? store.GetUserAsync(id, cancellationToken) : Task.FromResult<User?>(null);
    public Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        store.FindUserByNameAsync(normalizedUserName, cancellationToken);
    public Task<string> GetUserIdAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.Id.ToString());
    public Task<string?> GetUserNameAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.UserName);
    public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.NormalizedUserName);
    public Task SetUserNameAsync(User user, string? userName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.SetUserName(userName);
        return Task.CompletedTask;
    }
    public Task SetNormalizedUserNameAsync(User user, string? normalizedName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (user.NormalizedUserName != normalizedName) throw new InvalidOperationException("Username normalization is inconsistent.");
        return Task.CompletedTask;
    }
    public Task SetPasswordHashAsync(User user, string? passwordHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        user.SetPasswordHash(passwordHash ?? throw new InvalidOperationException("A password hash is required."));
        return Task.CompletedTask;
    }
    public Task<string?> GetPasswordHashAsync(User user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.PasswordHash);
    public Task<bool> HasPasswordAsync(User user, CancellationToken cancellationToken) => Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));
    public Task<IdentityResult> UpdateAsync(User user, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Use the authorized Identity application commands to update users.");
    public Task<IdentityResult> DeleteAsync(User user, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Startup bootstrap cannot delete users.");
    public void Dispose() { }
}
