using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MyOnlineShop.Identity.Application;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Domain;

namespace MyOnlineShop.Identity.Infrastructure.Security;

internal sealed class IdentityTokens(IOptions<IdentitySecurityOptions> options, TimeProvider clock) : IIdentityTokens
{
    private readonly IdentitySecurityOptions _options = options.Value;
    public TimeSpan RefreshLifetime => TimeSpan.FromDays(_options.RefreshTokenDays);
    public int MaximumFailedLogins => _options.MaximumFailedLogins;
    public TimeSpan LockoutDuration => TimeSpan.FromMinutes(_options.LockoutMinutes);
    public RefreshSecret CreateRefreshToken()
    {
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return new RefreshSecret(value, HashRefreshToken(value));
    }
    public string HashRefreshToken(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public TokenPair Issue(User user, RefreshSession session, IReadOnlyList<string> roles, RefreshSecret refresh)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(_options.AccessTokenMinutes);
        if (session.ExpiresAtUtc < expires) expires = session.ExpiresAtUtc;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new("sid", session.Id.ToString()), new("stamp", user.SecurityStamp.ToString())
        };
        claims.AddRange(roles.Select(role => new Claim("role", role)));
        var jwt = new JwtSecurityToken(_options.Issuer, _options.Audience, claims,
            now.UtcDateTime, expires.UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(_options.SigningKeyBase64)), SecurityAlgorithms.HmacSha256));
        return new TokenPair
        {
            AccessToken = new JwtSecurityTokenHandler().WriteToken(jwt), RefreshToken = refresh.Value,
            AccessTokenExpiresAtUtc = expires, RefreshTokenExpiresAtUtc = session.ExpiresAtUtc
        };
    }
}
