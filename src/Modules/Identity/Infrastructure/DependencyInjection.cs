using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Identity.Application;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Domain;
using MyOnlineShop.Identity.Infrastructure.Persistence;
using MyOnlineShop.Identity.Infrastructure.Security;

namespace MyOnlineShop.Identity.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<IdentitySecurityOptions>().Bind(configuration.GetSection(IdentitySecurityOptions.SectionName))
            .Validate(value => !string.IsNullOrWhiteSpace(value.Issuer) && value.Issuer.Length <= 128, "Identity issuer is required.")
            .Validate(value => !string.IsNullOrWhiteSpace(value.Audience) && value.Audience.Length <= 128, "Identity audience is required.")
            .Validate(IdentitySecurityOptions.HasValidKey, "Identity signing key must be base64-encoded with at least 32 random bytes.")
            .Validate(value => value.AccessTokenMinutes is >= 1 and <= 30 && value.RefreshTokenDays is >= 1 and <= 30,
                "Identity token lifetimes are outside permitted limits.")
            .Validate(value => value.MaximumFailedLogins is >= 3 and <= 10 && value.LockoutMinutes is >= 1 and <= 60,
                "Identity lockout settings are outside permitted limits.")
            .ValidateOnStart();
        services.AddDbContext<IdentityDbContext>((provider, options) =>
        {
            var database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var connection = configuration.GetConnectionString("SqlServer");
            options.UseSqlServer(connection, sql =>
            {
                sql.CommandTimeout(database.CommandTimeoutSeconds);
                sql.EnableRetryOnFailure(database.MaxRetryCount);
                sql.MigrationsAssembly(typeof(IdentityDbContext).Assembly.FullName);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "identity");
            });
        });
        services.Configure<PasswordHasherOptions>(options => options.IterationCount = 600_000);
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton<IIdentityPasswords, IdentityPasswords>();
        services.AddSingleton<IIdentityTokens, IdentityTokens>();
        services.AddScoped<IIdentityStore, IdentityStore>();
        services.AddScoped<IIdentityCommands, AuthenticationCommands>();
        services.AddScoped<IIdentityAdministration, AdministrationCommands>();
        services.AddScoped<IIdentityQueries, IdentityQueries>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<IdentitySecurityOptions>>((jwt, configured) =>
            {
                var security = configured.Value;
                jwt.MapInboundClaims = false;
                jwt.IncludeErrorDetails = false;
                jwt.SaveToken = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = security.Issuer,
                    ValidateAudience = true, ValidAudience = security.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(security.SigningKeyBase64)),
                    RequireSignedTokens = true, RequireExpirationTime = true, ValidateLifetime = true,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256], ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub", RoleClaimType = "role"
                };
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal!;
                        if (!TryGuidClaim(principal, "sub", out var userId) ||
                            !TryGuidClaim(principal, "sid", out var sessionId) ||
                            !TryGuidClaim(principal, "stamp", out var stamp) ||
                            !TryGuidClaim(principal, "jti", out _))
                        {
                            context.Fail("Required identity claims are invalid.");
                            return;
                        }
                        var store = context.HttpContext.RequestServices.GetRequiredService<IIdentityStore>();
                        var clock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
                        var issuedAtClaims = principal.FindAll("iat").ToArray();
                        if (issuedAtClaims.Length != 1 || !long.TryParse(issuedAtClaims[0].Value,
                            NumberStyles.None, CultureInfo.InvariantCulture, out var issuedAt) || issuedAt < 0 ||
                            issuedAt > clock.GetUtcNow().AddSeconds(30).ToUnixTimeSeconds())
                        {
                            context.Fail("Token issuance claim is invalid.");
                            return;
                        }
                        var snapshot = await store.GetAuthorizationAsync(userId, sessionId, clock.GetUtcNow(), context.HttpContext.RequestAborted);
                        if (snapshot is null || snapshot.SecurityStamp != stamp)
                        {
                            context.Fail("Identity session is inactive.");
                            return;
                        }
                        // Persisted assignments are authoritative, including changes made after JWT issuance.
                        var identity = (ClaimsIdentity)principal.Identity!;
                        foreach (var claim in identity.FindAll("role").Concat(identity.FindAll("permission")).ToArray())
                            identity.RemoveClaim(claim);
                        identity.AddClaims(snapshot.Roles.Select(role => new Claim("role", role)));
                        identity.AddClaims(snapshot.Permissions.Select(permission => new Claim("permission", permission)));
                    }
                };
            });
        return services;
    }

    private static bool TryGuidClaim(ClaimsPrincipal principal, string type, out Guid value)
    {
        value = Guid.Empty;
        var claims = principal.FindAll(type).ToArray();
        return claims.Length == 1 && Guid.TryParse(claims[0].Value, out value) && value != Guid.Empty;
    }
}
