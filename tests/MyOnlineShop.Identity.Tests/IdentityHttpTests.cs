using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Application;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Identity.Tests;

public sealed class IdentityHttpFactory : WebApplicationFactory<Program>
{
    public InMemoryIdentityStore Store { get; } = new();
    public CapturedLogs Logs { get; } = new();
    public string Key { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // This provider configuration is used only for offline model/script checks; no connection is opened.
        builder.UseSetting("ConnectionStrings:SqlServer",
            "Server=localhost;Database=IdentityOfflineTests;Integrated Security=True");
        builder.UseSetting("IdentitySecurity:SigningKeyBase64", Key);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IIdentityStore>();
            services.AddSingleton<IIdentityStore>(Store);
            services.AddControllers().AddApplicationPart(typeof(IdentityProbeController).Assembly);
        });
    }
    public HttpClient Client(string? token = null)
    {
        var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
}

public sealed partial class IdentityHttpTests
{
    private const string Password = "Http-Test-Password1!";
    private static async Task<UserDto> RegisterAsync(HttpClient client, string email = "customer@example.test", string? phone = null)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/identity/register", new RegisterCommand
        {
            FirstName = "First", LastName = "Last", Email = email, Password = Password, PhoneNumber = phone
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResponse<UserDto>>())!.Data;
    }
    private static async Task<TokenPair> LoginAsync(HttpClient client, string email = "customer@example.test")
    {
        using var response = await client.PostAsJsonAsync("/api/v1/identity/login", new LoginCommand { Email = email, Password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return (await response.Content.ReadFromJsonAsync<ApiResponse<TokenPair>>())!.Data;
    }

    [Fact]
    public async Task Registration_login_and_jwt_authentication_reuse_foundation_contracts()
    {
        using var factory = new IdentityHttpFactory();
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "identity-http-test");
        var user = await RegisterAsync(client);
        Assert.Equal([IdentityPermissions.CustomerRole], user.Roles);
        var tokens = await LoginAsync(client);
        using var authorized = factory.Client(tokens.AccessToken);
        using var me = await authorized.GetAsync("/api/v1/identity/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(user.Id, (await me.Content.ReadFromJsonAsync<ApiResponse<UserDto>>())!.Data.Id);
        Assert.NotEqual(Password, factory.Store.Users[user.Id].PasswordHash);
        Assert.DoesNotContain("passwordHash", await me.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tokens.RefreshToken, factory.Store.RefreshTokens.Select(token => token.TokenHash));
        var logs = string.Join("\n", factory.Logs.Messages);
        Assert.DoesNotContain(Password, logs);
        Assert.DoesNotContain(tokens.RefreshToken, logs);
        Assert.DoesNotContain(tokens.AccessToken, logs);
        Assert.Contains(factory.Store.Audits, audit => audit.Action == "Registration" && audit.CorrelationId == "identity-http-test");
    }

    [Fact]
    public async Task Invalid_credentials_and_lockout_are_safe_and_audited()
    {
        using var factory = new IdentityHttpFactory();
        using var client = factory.Client();
        await RegisterAsync(client);
        foreach (var email in new[] { "customer@example.test", "missing@example.test" })
        {
            using var response = await client.PostAsJsonAsync("/api/v1/identity/login", new LoginCommand { Email = email, Password = "wrong-pass" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("invalid_credentials", await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain(email, await response.Content.ReadAsStringAsync());
        }
        for (var attempt = 0; attempt < 4; attempt++)
            await client.PostAsJsonAsync("/api/v1/identity/login", new LoginCommand { Email = "customer@example.test", Password = "wrong-pass" });
        using var locked = await client.PostAsJsonAsync("/api/v1/identity/login", new LoginCommand { Email = "customer@example.test", Password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains(factory.Store.Audits, audit => audit.Action == "Login" && audit.Outcome == "Denied");
    }

    [Fact]
    public async Task Rotation_replay_and_logout_revoke_access_and_refresh()
    {
        using var factory = new IdentityHttpFactory();
        using var client = factory.Client();
        await RegisterAsync(client);
        var original = await LoginAsync(client);
        using var refresh = await client.PostAsJsonAsync("/api/v1/identity/refresh", new RefreshCommand { RefreshToken = original.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var rotated = (await refresh.Content.ReadFromJsonAsync<ApiResponse<TokenPair>>())!.Data;
        Assert.NotEqual(original.RefreshToken, rotated.RefreshToken);
        Assert.Equal(original.RefreshTokenExpiresAtUtc, rotated.RefreshTokenExpiresAtUtc);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/identity/refresh", new RefreshCommand { RefreshToken = original.RefreshToken })).StatusCode);
        using var revoked = factory.Client(rotated.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.GetAsync("/api/v1/identity/me")).StatusCode);
        var session = await LoginAsync(client);
        var logout = new RefreshCommand { RefreshToken = session.RefreshToken };
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/identity/logout", logout)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/identity/logout", logout)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/identity/refresh", logout)).StatusCode);
        using var loggedOut = factory.Client(session.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await loggedOut.GetAsync("/api/v1/identity/me")).StatusCode);
    }

    [Fact]
    public async Task Permission_changes_and_activation_have_immediate_effect_and_audit()
    {
        using var factory = new IdentityHttpFactory();
        using var anonymous = factory.Client();
        var user = await RegisterAsync(anonymous);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IIdentityAdministration>().BootstrapAdministratorAsync(new RegisterCommand
            {
                FirstName = "Test", LastName = "Admin", Email = "admin@example.test", Password = Password
            }, CancellationToken.None);
        using var admin = factory.Client((await LoginAsync(anonymous, "admin@example.test")).AccessToken);
        using var customer = factory.Client((await LoginAsync(anonymous)).AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/identity/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/identity-probe/administrator")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/identity-probe/administrator")).StatusCode);
        using var create = await admin.PostAsJsonAsync("/api/v1/identity/roles", new CreateRoleCommand { Name = "Support" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var role = (await create.Content.ReadFromJsonAsync<ApiResponse<RoleDto>>())!.Data;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/v1/identity/roles/{role.Id}/permissions/{IdentityPermissions.ManageRoles}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/v1/identity/users/{user.Id}/roles/{role.Id}", null)).StatusCode);
        using var privileged = factory.Client((await LoginAsync(anonymous)).AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await privileged.GetAsync("/api/v1/identity/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/identity/roles/{role.Id}/permissions/{IdentityPermissions.ManageRoles}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await privileged.GetAsync("/api/v1/identity/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/identity/users/{user.Id}/status", new ChangeStatusCommand { IsActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await privileged.GetAsync("/api/v1/identity/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/identity/users/{user.Id}/status", new ChangeStatusCommand { IsActive = true })).StatusCode);
        await LoginAsync(anonymous);
        Assert.Contains(factory.Store.Audits, audit => audit.Action == "RoleAssigned");
        Assert.Contains(factory.Store.Audits, audit => audit.Action == "PermissionRemoved");
        Assert.Contains(factory.Store.Audits, audit => audit.Action == "UserDeactivated");
        Assert.Contains(factory.Store.Audits, audit => audit.Action == "UserActivated");
    }

    [Fact]
    public async Task Jwt_rejects_invalid_signature_expiration_issuer_audience_and_required_claims()
    {
        using var factory = new IdentityHttpFactory();
        using var anonymous = factory.Client();
        await RegisterAsync(anonymous);
        var original = new JwtSecurityTokenHandler().ReadJwtToken((await LoginAsync(anonymous)).AccessToken);
        foreach (var scenario in new[] { "expired", "issuer", "audience", "signature", "claims", "issued-at" })
        {
            var jwt = new JwtSecurityToken(scenario == "issuer" ? "WrongIssuer" : "MyOnlineShop",
                scenario == "audience" ? "WrongAudience" : "MyOnlineShop.Api",
                scenario == "claims" ? [] : original.Claims.Where(claim => claim.Type is not ("exp" or "nbf" or "aud" or "iss") &&
                    (scenario != "issued-at" || claim.Type != "iat")),
                DateTime.UtcNow.AddHours(-1), scenario == "expired" ? DateTime.UtcNow.AddMinutes(-2) : DateTime.UtcNow.AddMinutes(5),
                new SigningCredentials(new SymmetricSecurityKey(scenario == "signature" ? new byte[32] : Convert.FromBase64String(factory.Key)), SecurityAlgorithms.HmacSha256));
            using var client = factory.Client(new JwtSecurityTokenHandler().WriteToken(jwt));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/identity/me")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_profile_and_invalid_registration_return_safe_errors()
    {
        using var factory = new IdentityHttpFactory();
        using var client = factory.Client();
        await RegisterAsync(client, phone: "+989121234567");
        foreach (var command in new[]
        {
            new RegisterCommand { FirstName = "Test", LastName = "User", Email = "CUSTOMER@example.test", Password = Password },
            new RegisterCommand { FirstName = "Test", LastName = "User", Email = "other@example.test", Password = Password, PhoneNumber = "+989121234567" }
        })
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/identity/register", command)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/identity/register", new { FirstName = "Test", LastName = "User", Email = "test@example.test", Password = "weak" })).StatusCode);
    }

    [Fact]
    public void Sql_server_model_and_migration_script_have_identity_ownership_and_unique_constraints()
    {
        using var factory = new IdentityHttpFactory();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", context.Database.ProviderName);
        Assert.All(context.Model.GetEntityTypes(), entity => Assert.Equal("identity", entity.GetSchema()));
        Assert.Contains(context.Database.GetMigrations(), migration => migration.EndsWith("_InitialIdentity", StringComparison.Ordinal));
        Assert.Contains(context.Database.GetMigrations(), migration => migration.EndsWith("_AddCatalogPermission", StringComparison.Ordinal));
        var script = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Users_NormalizedEmail]", script);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Users_PhoneNumber]", script);
        Assert.Contains("CREATE UNIQUE INDEX [IX_RefreshTokens_TokenHash]", script);
        Assert.DoesNotContain("DROP TABLE", script);
    }

    [Fact]
    public async Task Public_registration_cannot_assign_roles_and_status_requires_explicit_value()
    {
        using var factory = new IdentityHttpFactory();
        using var client = factory.Client();
        using var registered = await client.PostAsJsonAsync("/api/v1/identity/register", new
        {
            FirstName = "First", LastName = "Last", Email = "customer@example.test", Password,
            Roles = new[] { "Administrator" }, IsActive = true
        });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var user = (await registered.Content.ReadFromJsonAsync<ApiResponse<UserDto>>())!.Data;
        Assert.Equal([IdentityPermissions.CustomerRole], user.Roles);
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IIdentityAdministration>().BootstrapAdministratorAsync(new RegisterCommand
        {
            FirstName = "Test", LastName = "Admin", Email = "admin@example.test", Password = Password
        }, CancellationToken.None);
        using var admin = factory.Client((await LoginAsync(client, "admin@example.test")).AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/identity/users/{user.Id}/status", new { })).StatusCode);
        Assert.True(factory.Store.Users[user.Id].Status == MyOnlineShop.Identity.Domain.UserStatus.Active);
    }
}
