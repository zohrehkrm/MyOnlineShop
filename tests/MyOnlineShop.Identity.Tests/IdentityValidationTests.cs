using MyOnlineShop.Identity.Application;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Domain;
using MyOnlineShop.Identity.Infrastructure.Security;
using Xunit;

namespace MyOnlineShop.Identity.Tests;

public sealed class IdentityValidationTests
{
    [Theory]
    [InlineData("short1!A")]
    [InlineData("alllowercase123!")]
    [InlineData("ALLUPPERCASE123!")]
    [InlineData("NoDigitsPassword!")]
    [InlineData("NoSymbolsPassword1")]
    public void Weak_passwords_are_rejected(string password)
    {
        var exception = Assert.Throws<IdentityException>(() => IdentityValidation.Validate(new RegisterCommand
        {
            FirstName = "Test", LastName = "User", Email = "test@example.test", Password = password
        }));
        Assert.Equal(400, exception.StatusCode);
    }

    [Theory]
    [InlineData("", "Last", "test@example.test", null)]
    [InlineData("   ", "Last", "test@example.test", null)]
    [InlineData("First", "", "test@example.test", null)]
    [InlineData("First", "Last", "not-email", null)]
    [InlineData("First", "Last", "test@example.test", "09121234567")]
    public void Required_profile_and_phone_format_are_validated(string first, string last, string email, string? phone)
    {
        Assert.Throws<IdentityException>(() => IdentityValidation.Validate(new RegisterCommand
        {
            FirstName = first, LastName = last, Email = email, PhoneNumber = phone, Password = IdentityFactory.TestPassword
        }));
    }

    [Fact]
    public void Optional_phone_and_strong_password_are_accepted()
    {
        IdentityValidation.Validate(new RegisterCommand
        {
            FirstName = "Test", LastName = "User", Email = "test@example.test", Password = IdentityFactory.TestPassword
        });
    }

    [Fact]
    public void Domain_rejects_missing_names_and_invalid_status()
    {
        Assert.Throws<ArgumentException>(() => User.Create("test@example.test", " ", "User", null, DateTimeOffset.UtcNow));
        var user = User.Create("test@example.test", "Test", "User", null, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentOutOfRangeException>(() => user.ChangeStatus((UserStatus)999));
    }

    [Fact]
    public void Security_configuration_rejects_missing_short_and_malformed_keys()
    {
        Assert.False(IdentitySecurityOptions.HasValidKey(new()));
        Assert.False(IdentitySecurityOptions.HasValidKey(new() { SigningKeyBase64 = "not-base64" }));
        Assert.False(IdentitySecurityOptions.HasValidKey(new() { SigningKeyBase64 = Convert.ToBase64String(new byte[16]) }));
    }

    [Fact]
    public void Login_validation_rejects_missing_or_oversized_passwords()
    {
        Assert.Throws<IdentityException>(() => IdentityValidation.Validate(new LoginCommand { Email = "test@example.test" }));
        Assert.Throws<IdentityException>(() => IdentityValidation.Validate(new LoginCommand { Email = "test@example.test", Password = new string('x', 129) }));
    }

    [Fact]
    public void Lockout_expires_and_successful_login_resets_failure_counter()
    {
        var now = DateTimeOffset.UtcNow;
        var user = User.Create("test@example.test", "Test", "User", null, now);
        for (var attempt = 0; attempt < 5; attempt++) user.RecordFailedLogin(now, 5, TimeSpan.FromMinutes(15));
        Assert.False(user.CanLogin(now));
        Assert.True(user.CanLogin(now.AddMinutes(16)));
        user.RecordFailedLogin(now.AddMinutes(16), 5, TimeSpan.FromMinutes(15));
        Assert.Equal(1, user.FailedLoginAttempts);
        user.RecordSuccessfulLogin();
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockoutUntilUtc);
    }
}
