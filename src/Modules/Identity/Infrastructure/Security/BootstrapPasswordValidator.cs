using Microsoft.AspNetCore.Identity;
using MyOnlineShop.BuildingBlocks.Abstractions.Localization;
using MyOnlineShop.Identity.Domain;

namespace MyOnlineShop.Identity.Infrastructure.Security;

public sealed class BootstrapPasswordValidator : IPasswordValidator<User>
{
    public Task<IdentityResult> ValidateAsync(UserManager<User> manager, User user, string? password)
    {
        var valid = password is { Length: >= 8 and <= 128 } && new PasswordComplexityAttribute().IsValid(password);
        return Task.FromResult(valid ? IdentityResult.Success : IdentityResult.Failed(new IdentityError
        {
            Code = "BootstrapPasswordPolicy",
            Description = "The bootstrap password must be 8–128 characters, contain upper/lowercase letters, a digit and symbol, and have no whitespace."
        }));
    }
}
