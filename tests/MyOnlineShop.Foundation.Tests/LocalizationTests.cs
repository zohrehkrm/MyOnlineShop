using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MyOnlineShop.Foundation.Tests;

public sealed class LocalizationTests(FoundationFactory factory) : IClassFixture<FoundationFactory>
{
    [Theory]
    [InlineData(null, "fa-IR", "رمز عبور", "یک یا چند فیلد")]
    [InlineData("en-US", "en-US", "Password", "One or more fields")]
    [InlineData("de-DE", "fa-IR", "رمز عبور", "یک یا چند فیلد")]
    public async Task Registration_validation_is_localized_and_explains_password_rules(
        string? language, string expectedCulture, string fieldName, string title)
    {
        using var client = factory.CreateHttpsClient();
        if (language is not null) client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        using var response = await client.PostAsJsonAsync("/api/v1/identity/register", new
        {
            firstName = "zohreh", lastName = "karimi", email = "zohreh@example.test", password = "asdfg"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedCulture, response.Content.Headers.ContentLanguage);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(title, json.RootElement.GetProperty("title").GetString());
        var errors = json.RootElement.GetProperty("errors").GetProperty("Password").EnumerateArray()
            .Select(error => error.GetString()!).ToArray();
        Assert.Contains(errors, error => error.Contains(fieldName) && error.Contains("8") && error.Contains("128"));
        Assert.Equal(2, errors.Length);
        Assert.DoesNotContain("asdfg", string.Join(" ", errors));
    }

    [Fact]
    public async Task Query_culture_overrides_header_and_requests_do_not_share_culture()
    {
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fa-IR");
        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/foundation-probe/validate?culture=en-US", new { }),
            client.PostAsJsonAsync("/foundation-probe/validate", new { }));
        try
        {
            var english = await responses[0].Content.ReadAsStringAsync();
            var persian = await responses[1].Content.ReadAsStringAsync();
            using var en = JsonDocument.Parse(english);
            using var fa = JsonDocument.Parse(persian);
            Assert.Equal("Name is required.", en.RootElement.GetProperty("errors").GetProperty("Name")[0].GetString());
            Assert.Contains("الزامی", fa.RootElement.GetProperty("errors").GetProperty("Name")[0].GetString());
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData("/missing", "not_found", "پیدا نشد", "not found")]
    [InlineData("/api/v1/identity/me", "unauthorized", "وارد", "Sign in")]
    [InlineData("/foundation-probe/throw", "internal_error", "خطای داخلی", "internal server error")]
    [InlineData("/foundation-probe/application-error", "invalid_credentials", "ایمیل یا رمز عبور", "email or password")]
    public async Task Http_and_application_errors_are_localized(string path, string code, string faText, string enText)
    {
        using var client = factory.CreateHttpsClient();
        foreach (var (culture, expected) in new[] { ("fa-IR", faText), ("en-US", enText) })
        {
            using var response = await client.GetAsync(path + "?culture=" + culture);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
            Assert.Contains(expected, json.RootElement.GetProperty("title").GetString());
            Assert.True(json.RootElement.TryGetProperty("correlationId", out _));
        }
    }

    [Fact]
    public async Task Malformed_json_returns_localized_safe_error()
    {
        using var client = factory.CreateHttpsClient();
        using var body = new StringContent("{ private-input", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/foundation-probe/validate", body);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = json.RootElement.GetProperty("errors").EnumerateObject()
            .SelectMany(field => field.Value.EnumerateArray()).Select(error => error.GetString()!).ToArray();
        Assert.Contains(errors, error => error.Contains("JSON"));
        Assert.DoesNotContain(errors, error => error.Contains("private-input"));
    }

    [Fact]
    public async Task Application_validation_keeps_field_errors()
    {
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/foundation-probe/application-validation");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_error", json.RootElement.GetProperty("code").GetString());
        var errors = json.RootElement.GetProperty("errors").GetProperty("Password");
        Assert.Equal(2, errors.GetArrayLength());
        Assert.Contains("رمز عبور", errors[0].GetString());
    }
}
