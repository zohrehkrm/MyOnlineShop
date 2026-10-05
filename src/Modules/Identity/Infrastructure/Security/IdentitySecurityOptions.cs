namespace MyOnlineShop.Identity.Infrastructure.Security;

public sealed class IdentitySecurityOptions
{
    public const string SectionName = "IdentitySecurity";
    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string SigningKeyBase64 { get; init; } = string.Empty;
    public int AccessTokenMinutes { get; init; } = 10;
    public int RefreshTokenDays { get; init; } = 30;
    public int MaximumFailedLogins { get; init; } = 5;
    public int LockoutMinutes { get; init; } = 15;

    public static bool HasValidKey(IdentitySecurityOptions options)
    {
        if (options.SigningKeyBase64.Length > 256) return false;
        Span<byte> bytes = stackalloc byte[192];
        return Convert.TryFromBase64String(options.SigningKeyBase64, bytes, out var written) && written >= 32;
    }
}
