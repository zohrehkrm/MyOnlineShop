using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Identity.Tests;

public sealed class IdentityIntegrationTests(IdentityFactory factory) : IClassFixture<IdentityFactory>
{
    [IdentitySqlFact]
    public async Task Registration_persists_hashed_password_required_profile_and_default_role()
    {
        var (user, _) = await factory.RegisterAsync();
        Assert.Equal("Test", user.FirstName);
        Assert.True(user.IsActive);
        Assert.Null(user.PhoneNumber);
        Assert.Equal([IdentityPermissions.CustomerRole], user.Roles);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var stored = await context.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == user.Id);
        Assert.NotEqual(IdentityFactory.TestPassword, stored.PasswordHash);
        Assert.NotEmpty(stored.PasswordHash);
        Assert.True(await context.Audit.AnyAsync(audit => audit.TargetUserId == user.Id && audit.Action == "Registration"));
    }

    [IdentitySqlFact]
    public async Task Email_uniqueness_is_case_insensitive_and_phone_is_unique()
    {
        var (_, email) = await factory.RegisterAsync("+989121234567");
        using var client = factory.Client();
        using var emailConflict = await client.PostAsJsonAsync("/api/v1/identity/register", new RegisterCommand
        {
            FirstName = "Another", LastName = "User", Email = email.ToUpperInvariant(), Password = IdentityFactory.TestPassword
        });
        Assert.Equal(HttpStatusCode.Conflict, emailConflict.StatusCode);
        using var phoneConflict = await client.PostAsJsonAsync("/api/v1/identity/register", new RegisterCommand
        {
            FirstName = "Another", LastName = "User", Email = Guid.NewGuid() + "@example.test",
            Password = IdentityFactory.TestPassword, PhoneNumber = "+989121234567"
        });
        Assert.Equal(HttpStatusCode.Conflict, phoneConflict.StatusCode);
    }

    [IdentitySqlFact]
    public async Task Login_returns_bounded_tokens_with_no_store_and_jwt_authenticates()
    {
        var (user, email) = await factory.RegisterAsync();
        using var anonymous = factory.Client();
        using var login = await anonymous.PostAsJsonAsync("/api/v1/identity/login", new LoginCommand { Email = email, Password = IdentityFactory.TestPassword });
        Assert.True(login.Headers.CacheControl!.NoStore);
        var tokens = (await login.Content.ReadFromJsonAsync<ApiResponse<TokenPair>>())!.Data;
        Assert.Equal(64, tokens.RefreshToken.Length);
        Assert.InRange(tokens.AccessTokenExpiresAtUtc, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(11));
        using var client = factory.Client(tokens.AccessToken);
        using var me = await client.GetAsync("/api/v1/identity/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(user.Id, (await me.Content.ReadFromJsonAsync<ApiResponse<UserDto>>())!.Data.Id);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.False(await context.RefreshTokens.AnyAsync(token => token.TokenHash == tokens.RefreshToken));
        Assert.DoesNotContain(tokens.AccessToken, string.Join("\n", factory.Logs.Messages));
        Assert.DoesNotContain(tokens.RefreshToken, string.Join("\n", factory.Logs.Messages));
        Assert.DoesNotContain(IdentityFactory.TestPassword, string.Join("\n", factory.Logs.Messages));
    }

    [IdentitySqlFact]
    public async Task Invalid_credentials_are_generic_and_login_failures_are_audited()
    {
        var (user, email) = await factory.RegisterAsync();
        using var client = factory.Client();
        using var wrong = await client.PostAsJsonAsync("/api/v1/identity/login", new LoginCommand { Email = email, Password = "Wrong-password1!" });
        using var missing = await client.PostAsJsonAsync("/api/v1/identity/login", new LoginCommand { Email = "unknown@example.test", Password = "Wrong-password1!" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using var wrongBody = JsonDocument.Parse(await wrong.Content.ReadAsStringAsync());
        using var missingBody = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        Assert.Equal(wrongBody.RootElement.GetProperty("title").GetString(), missingBody.RootElement.GetProperty("title").GetString());
        Assert.Equal("invalid_credentials", wrongBody.RootElement.GetProperty("code").GetString());
        using var scope = factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Audit
            .AnyAsync(audit => audit.TargetUserId == user.Id && audit.Action == "Login" && audit.Outcome == "Denied"));
    }

    [IdentitySqlFact]
    public async Task Lockout_blocks_correct_password_after_repeated_failures()
    {
        var (_, email) = await factory.RegisterAsync();
        using var client = factory.Client();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var denied = await client.PostAsJsonAsync("/api/v1/identity/login", new LoginCommand { Email = email, Password = "Wrong-password1!" });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var locked = await client.PostAsJsonAsync("/api/v1/identity/login", new LoginCommand { Email = email, Password = IdentityFactory.TestPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
    }

    [IdentitySqlFact]
    public async Task Refresh_rotates_and_replay_revokes_the_entire_session()
    {
        var (_, email) = await factory.RegisterAsync();
        var original = await factory.LoginAsync(email);
        using var client = factory.Client();
        using var refresh = await client.PostAsJsonAsync("/api/v1/identity/refresh", new RefreshCommand { RefreshToken = original.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var rotated = (await refresh.Content.ReadFromJsonAsync<ApiResponse<TokenPair>>())!.Data;
        Assert.NotEqual(original.RefreshToken, rotated.RefreshToken);
        Assert.Equal(original.RefreshTokenExpiresAtUtc, rotated.RefreshTokenExpiresAtUtc);
        using var replay = await client.PostAsJsonAsync("/api/v1/identity/refresh", new RefreshCommand { RefreshToken = original.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var denied = await client.PostAsJsonAsync("/api/v1/identity/refresh", new RefreshCommand { RefreshToken = rotated.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var authenticated = factory.Client(rotated.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await authenticated.GetAsync("/api/v1/identity/me")).StatusCode);
    }

    [IdentitySqlFact]
    public async Task Concurrent_refresh_has_one_winner_and_detects_replay()
    {
        var (_, email) = await factory.RegisterAsync();
        var tokens = await factory.LoginAsync(email);
        using var first = factory.Client();
        using var second = factory.Client();
        var command = new RefreshCommand { RefreshToken = tokens.RefreshToken };
        var responses = await Task.WhenAll(first.PostAsJsonAsync("/api/v1/identity/refresh", command),
            second.PostAsJsonAsync("/api/v1/identity/refresh", command));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
        var winner = (await responses.Single(response => response.IsSuccessStatusCode).Content.ReadFromJsonAsync<ApiResponse<TokenPair>>())!.Data;
        using var client = factory.Client(winner.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me")).StatusCode);
        foreach (var response in responses) response.Dispose();
    }

    [IdentitySqlFact]
    public async Task Logout_is_idempotent_and_revokes_refresh_and_access_tokens()
    {
        var (_, email) = await factory.RegisterAsync();
        var tokens = await factory.LoginAsync(email);
        using var anonymous = factory.Client();
        var command = new RefreshCommand { RefreshToken = tokens.RefreshToken };
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/v1/identity/logout", command)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/v1/identity/logout", command)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/identity/refresh", command)).StatusCode);
        using var client = factory.Client(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me")).StatusCode);
    }

    [IdentitySqlFact]
    public async Task Permission_and_role_policies_enforce_persisted_assignments()
    {
        var (user, email) = await factory.RegisterAsync();
        var tokens = await factory.LoginAsync(email);
        using var customer = factory.Client(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/identity/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/identity-probe/administrator")).StatusCode);
        using var admin = factory.Client(factory.AdministratorTokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/identity-probe/administrator")).StatusCode);
        using var create = await admin.PostAsJsonAsync("/api/v1/identity/roles", new CreateRoleCommand { Name = "Support" + Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var role = (await create.Content.ReadFromJsonAsync<ApiResponse<RoleDto>>())!.Data;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/v1/identity/roles/{role.Id}/permissions/{IdentityPermissions.ManageRoles}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/v1/identity/users/{user.Id}/roles/{role.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await customer.GetAsync("/api/v1/identity/me")).StatusCode);
        using var authorized = factory.Client((await factory.LoginAsync(email)).AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await authorized.GetAsync("/api/v1/identity/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/identity/roles/{role.Id}/permissions/{IdentityPermissions.ManageRoles}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await authorized.GetAsync("/api/v1/identity/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/identity/users/{user.Id}/roles/{role.Id}")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var audits = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Audit.AsNoTracking()
            .Where(audit => audit.ActorUserId == factory.AdministratorId).Select(audit => audit.Action).ToListAsync();
        Assert.Contains("RoleAssigned", audits);
        Assert.Contains("RoleRemoved", audits);
        Assert.Contains("PermissionAssigned", audits);
        Assert.Contains("PermissionRemoved", audits);
    }

    [IdentitySqlFact]
    public async Task Deactivation_revokes_sessions_blocks_login_and_activation_is_audited()
    {
        var (user, email) = await factory.RegisterAsync();
        var tokens = await factory.LoginAsync(email);
        using var admin = factory.Client(factory.AdministratorTokens.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/identity/users/{user.Id}/status", new { IsActive = false })).StatusCode);
        using var customer = factory.Client(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await customer.GetAsync("/api/v1/identity/me")).StatusCode);
        using var anonymous = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/identity/login", new LoginCommand { Email = email, Password = IdentityFactory.TestPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/identity/refresh", new RefreshCommand { RefreshToken = tokens.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/identity/users/{user.Id}/status", new { IsActive = true })).StatusCode);
        await factory.LoginAsync(email);
        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Audit;
        Assert.True(await audit.AnyAsync(entry => entry.TargetUserId == user.Id && entry.Action == "UserDeactivated"));
        Assert.True(await audit.AnyAsync(entry => entry.TargetUserId == user.Id && entry.Action == "UserActivated"));
    }

    [IdentitySqlFact]
    public async Task Jwt_rejects_expiration_wrong_issuer_audience_signature_and_missing_claims()
    {
        var (_, email) = await factory.RegisterAsync();
        var original = new JwtSecurityTokenHandler().ReadJwtToken((await factory.LoginAsync(email)).AccessToken);
        foreach (var scenario in new[] { "expired", "issuer", "audience", "signature", "claims" })
        {
            var jwt = new JwtSecurityToken(scenario == "issuer" ? "WrongIssuer" : "MyOnlineShop",
                scenario == "audience" ? "WrongAudience" : "MyOnlineShop.Api",
                scenario == "claims" ? [] : original.Claims.Where(claim => claim.Type is not ("exp" or "nbf" or "aud" or "iss")),
                DateTime.UtcNow.AddHours(-1), scenario == "expired" ? DateTime.UtcNow.AddMinutes(-2) : DateTime.UtcNow.AddMinutes(5),
                new SigningCredentials(new SymmetricSecurityKey(scenario == "signature" ? new byte[32] : Convert.FromBase64String(factory.SigningKey)), SecurityAlgorithms.HmacSha256));
            using var client = factory.Client(new JwtSecurityTokenHandler().WriteToken(jwt));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me")).StatusCode);
        }
        using var anonymous = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/identity/me")).StatusCode);
    }

    [IdentitySqlFact]
    public async Task Expired_refresh_session_and_unknown_refresh_are_rejected()
    {
        var (user, email) = await factory.RegisterAsync();
        var tokens = await factory.LoginAsync(email);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Sessions.Where(session => session.UserId == user.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/identity/refresh", new RefreshCommand { RefreshToken = tokens.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/identity/refresh", new RefreshCommand { RefreshToken = new string('0', 64) })).StatusCode);
    }

    [IdentitySqlFact]
    public async Task Bootstrap_is_one_time_and_audit_is_append_only()
    {
        using var scope = factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IIdentityAdministration>();
        await Assert.ThrowsAsync<MyOnlineShop.Identity.Application.IdentityException>(() => admin.BootstrapAdministratorAsync(new RegisterCommand
        {
            FirstName = "Another", LastName = "Admin", Email = "another-admin@example.test", Password = IdentityFactory.TestPassword
        }, CancellationToken.None));
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var audit = await context.Audit.FirstAsync();
        context.Audit.Remove(audit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }
}
