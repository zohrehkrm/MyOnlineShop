using System.ComponentModel.DataAnnotations;

namespace MyOnlineShop.BuildingBlocks.Abstractions.Localization;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class PasswordComplexityAttribute : ValidationAttribute
{
    public PasswordComplexityAttribute()
    {
        ErrorMessageResourceType = typeof(ValidationMessages);
        ErrorMessageResourceName = nameof(ValidationMessages.PasswordComplexity);
    }

    public override bool IsValid(object? value) => value is null or "" ||
        value is string password && password.Any(character => character is >= 'A' and <= 'Z') &&
        password.Any(character => character is >= 'a' and <= 'z') && password.Any(char.IsDigit) &&
        password.Any(character => !char.IsLetterOrDigit(character) && !char.IsWhiteSpace(character)) &&
        !password.Any(char.IsWhiteSpace);
}
