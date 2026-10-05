using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using MyOnlineShop.Identity.Application;
using MyOnlineShop.Identity.Domain;

namespace MyOnlineShop.Identity.Infrastructure.Security;

internal sealed class IdentityPasswords : IIdentityPasswords
{
    private readonly IPasswordHasher<User> _hasher;
    private readonly User _dummy;
    public IdentityPasswords(IPasswordHasher<User> hasher)
    {
        _hasher = hasher;
        _dummy = User.Create("dummy@example.invalid", "Dummy", "User", null, DateTimeOffset.UnixEpoch);
        _dummy.SetPasswordHash(hasher.HashPassword(_dummy, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
    }
    public string Hash(User user, string password) => _hasher.HashPassword(user, password);
    public bool Verify(User? user, string password, out bool needsRehash)
    {
        var target = user ?? _dummy;
        var result = _hasher.VerifyHashedPassword(target, target.PasswordHash, password);
        needsRehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return user is not null && result != PasswordVerificationResult.Failed;
    }
}
