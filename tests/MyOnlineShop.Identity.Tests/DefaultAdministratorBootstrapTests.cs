using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyOnlineShop.Identity.Application;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Domain;
using MyOnlineShop.Identity.Infrastructure;
using MyOnlineShop.Identity.Infrastructure.Security;
using Xunit;

namespace MyOnlineShop.Identity.Tests;

public sealed class DefaultAdministratorBootstrapTests
{
    // Every successful bootstrap credential is generated for this test run; no default password is committed.
    private static string Password() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "aA1!";

    private static ServiceProvider Services(InMemoryIdentityStore store, string? password, bool enabled = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlServer"] = "Server=localhost;Database=BootstrapOffline;Integrated Security=True",
            ["IdentityBootstrap:Enabled"] = enabled.ToString(),
            ["IdentityBootstrap:Password"] = password
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdentityInfrastructure(config);
        services.RemoveAll<IIdentityStore>();
        services.AddSingleton<IIdentityStore>(store);
        return services.BuildServiceProvider();
    }

    private static async Task RunAsync(InMemoryIdentityStore store, string? password, bool enabled = true)
    {
        using var services = Services(store, password, enabled);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDefaultAdministratorBootstrap>().InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Repeated_bootstrap_creates_one_user_role_and_audit_with_a_verified_identity_hash()
    {
        var store = new InMemoryIdentityStore();
        var password = Password();
        await RunAsync(store, password);
        await RunAsync(store, password);
        var user = Assert.Single(store.Users.Values);
        Assert.Equal(DefaultAdministratorBootstrap.UserName, user.UserName);
        Assert.Equal(DefaultAdministratorBootstrap.Email, user.Email);
        Assert.Equal(DefaultAdministratorBootstrap.Phone, user.PhoneNumber);
        Assert.Equal(IdentityPermissions.AdministratorRoleId, Assert.Single(store.UserRoles).Role);
        Assert.Single(store.Audits);
        Assert.NotEqual(password, user.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success,
            new PasswordHasher<User>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 600_000 }))
                .VerifyHashedPassword(user, user.PasswordHash, password));
    }

    [Fact]
    public async Task Missing_password_fails_before_creating_any_user()
    {
        var store = new InMemoryIdentityStore();
        var error = await Assert.ThrowsAsync<IdentityBootstrapException>(() => RunAsync(store, null));
        Assert.Contains("IdentityBootstrap:Password is required", error.Message);
        Assert.Empty(store.Users);
        Assert.Empty(store.UserRoles);
    }

    [Fact]
    public async Task Disabled_bootstrap_requires_no_password_and_creates_no_user()
    {
        var store = new InMemoryIdentityStore();
        await RunAsync(store, null, enabled: false);
        Assert.Empty(store.Users);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("lowercase")]
    [InlineData("no-symbol")]
    [InlineData("whitespace")]
    [InlineData("too-long")]
    public async Task Invalid_password_fails_safely_before_creating_any_user(string failure)
    {
        var random = Password();
        var password = failure switch
        {
            "short" => random[..7],
            "lowercase" => random.ToLowerInvariant(),
            "no-symbol" => random.Replace("!", ""),
            "whitespace" => random + " ",
            _ => string.Concat(Enumerable.Repeat(random, 4))
        };
        var store = new InMemoryIdentityStore();
        var error = await Assert.ThrowsAsync<IdentityBootstrapException>(() => RunAsync(store, password));
        Assert.Contains("password policy", error.Message);
        Assert.DoesNotContain(password, error.ToString());
        Assert.Empty(store.Users);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Existing_username_or_email_on_another_account_fails_without_modifying_it(bool usernameConflict)
    {
        var store = new InMemoryIdentityStore();
        var other = User.Create(usernameConflict ? "other@example.test" : DefaultAdministratorBootstrap.Email,
            "Existing", "User", null, DateTimeOffset.UtcNow);
        other.SetUserName(usernameConflict ? DefaultAdministratorBootstrap.UserName : "other-user");
        var existingCredential = Password();
        other.SetPasswordHash(new PasswordHasher<User>().HashPassword(other, existingCredential));
        store.AddUser(other);
        var hash = other.PasswordHash;
        var error = await Assert.ThrowsAsync<IdentityBootstrapException>(() => RunAsync(store, Password()));
        Assert.Contains("conflicts", error.Message);
        Assert.Single(store.Users);
        Assert.Equal(hash, other.PasswordHash);
        Assert.Empty(store.UserRoles);
        Assert.Empty(store.Audits);
    }

    [Fact]
    public async Task Changed_bootstrap_password_preserves_existing_password_and_security_stamp()
    {
        var store = new InMemoryIdentityStore();
        await RunAsync(store, Password());
        var user = Assert.Single(store.Users.Values);
        var hash = user.PasswordHash;
        var stamp = user.SecurityStamp;
        await RunAsync(store, Password());
        Assert.Equal(hash, user.PasswordHash);
        Assert.Equal(stamp, user.SecurityStamp);
        Assert.Single(store.Audits);
    }

    [Fact]
    public async Task Missing_administrator_permission_fails_without_inventing_a_role_or_permission()
    {
        var store = new InMemoryIdentityStore();
        store.RolePermissions.Clear();
        var error = await Assert.ThrowsAsync<IdentityBootstrapException>(() => RunAsync(store, Password()));
        Assert.Contains("Administrator role", error.Message);
        Assert.Empty(store.Users);
        Assert.Empty(store.RolePermissions);
    }

    [Fact]
    public async Task Concurrent_repeated_bootstrap_is_serialized_by_the_existing_store_transaction()
    {
        var store = new InMemoryIdentityStore();
        var password = Password();
        await Task.WhenAll(Task.Run(() => RunAsync(store, password)), Task.Run(() => RunAsync(store, password)));
        Assert.Single(store.Users);
        Assert.Single(store.UserRoles);
        Assert.Single(store.Audits);
    }

    [Fact]
    public async Task Existing_inactive_account_is_not_reactivated_or_promoted()
    {
        var store = new InMemoryIdentityStore();
        await RunAsync(store, Password());
        var user = Assert.Single(store.Users.Values);
        user.ChangeStatus(UserStatus.Inactive);
        store.UserRoles.Clear();
        await Assert.ThrowsAsync<IdentityBootstrapException>(() => RunAsync(store, Password()));
        Assert.Equal(UserStatus.Inactive, user.Status);
        Assert.Empty(store.UserRoles);
    }
}
