using System.Globalization;
using System.Resources;

namespace MyOnlineShop.BuildingBlocks.Abstractions.Localization;

public static class ValidationMessages
{
    private static readonly ResourceManager Resources = new(typeof(ValidationMessages));
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture)
        ?? throw new InvalidOperationException($"Missing validation resource: {key}");
    public static string Required => Get(nameof(Required));
    public static string StringLength => Get(nameof(StringLength));
    public static string MaxLength => Get(nameof(MaxLength));
    public static string MinLength => Get(nameof(MinLength));
    public static string EmailAddress => Get(nameof(EmailAddress));
    public static string Range => Get(nameof(Range));
    public static string RegularExpression => Get(nameof(RegularExpression));
    public static string PhoneNumber => Get(nameof(PhoneNumber));
    public static string PasswordComplexity => Get(nameof(PasswordComplexity));
    public static string InvalidValue => Get(nameof(InvalidValue));
    public static string InvalidBody => Get(nameof(InvalidBody));
    public static string FirstName => Get(nameof(FirstName));
    public static string LastName => Get(nameof(LastName));
    public static string Email => Get(nameof(Email));
    public static string Password => Get(nameof(Password));
    public static string RefreshToken => Get(nameof(RefreshToken));
    public static string Name => Get(nameof(Name));
    public static string IsActive => Get(nameof(IsActive));
}
