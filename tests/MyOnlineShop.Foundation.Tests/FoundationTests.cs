using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Foundation;
using Xunit;

namespace MyOnlineShop.Foundation.Tests;

public sealed class FoundationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("IdentitySecurity:SigningKeyBase64", Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        builder.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(FoundationProbeController).Assembly));
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });
}

// Test-only routes verify the real pipeline without adding diagnostic endpoints to production.
[ApiController]
[Route("foundation-probe")]
public sealed class FoundationProbeController : ControllerBase
{
    [HttpGet("throw")]
    public IActionResult Throw() => throw new InvalidOperationException("secret-password");

    [HttpGet("context")]
    public IActionResult Context([FromServices] IRequestContext context) => Ok(context.CorrelationId);

    [HttpGet("not-found")]
    public IActionResult Missing() => NotFound();

    [HttpPost("validate")]
    public IActionResult Validate(ProbeRequest request) => Ok(request);
}

public sealed class ProbeRequest
{
    [Required]
    public string? Name { get; init; }
}

public sealed class FoundationTests(FoundationFactory factory) : IClassFixture<FoundationFactory>
{
    [Fact]
    public async Task Health_preserves_correlation_in_header_and_success_response()
    {
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Add(CorrelationIdMiddleware.HeaderName, "checkout-test-123");
        using var response = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("checkout-test-123", response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<HealthResponse>>();
        Assert.Equal("checkout-test-123", body!.CorrelationId);
        Assert.Equal("Healthy", body.Data.Status);
    }

    [Theory]
    [InlineData("invalid value")]
    [InlineData("invalid/value")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz")]
    public async Task Invalid_correlation_is_replaced(string supplied)
    {
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, supplied);
        using var response = await client.GetAsync("/api/v1/health");
        var actual = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.NotEqual(supplied, actual);
        Assert.True(Guid.TryParseExact(actual, "N", out _));
    }

    [Fact]
    public async Task Scoped_request_context_matches_generated_header()
    {
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/foundation-probe/context");
        Assert.Equal(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single(),
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unexpected_exception_returns_safe_correlated_problem()
    {
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Add(CorrelationIdMiddleware.HeaderName, "error-test");
        using var response = await client.GetAsync("/foundation-probe/throw");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-password", text);
        Assert.DoesNotContain("InvalidOperationException", text);
        using var document = JsonDocument.Parse(text);
        Assert.Equal("error-test", document.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("internal_error", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Validation_failure_has_distinct_code_and_field_errors()
    {
        using var client = factory.CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/foundation-probe/validate", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_error", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("Name", out _));
        Assert.Equal(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single(),
            document.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Missing_route_returns_correlated_problem()
    {
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/missing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task Controller_client_errors_use_the_same_problem_model()
    {
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/foundation-probe/not-found");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single(),
            document.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Malformed_json_returns_safe_validation_problem()
    {
        using var client = factory.CreateHttpsClient();
        using var content = new StringContent("{ broken-secret-value", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/foundation-probe/validate", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("broken-secret-value", body);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("validation_error", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Development_serves_openapi_and_swagger()
    {
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/health", out _));
        using var swagger = await client.GetAsync("/swagger/index.html");
        swagger.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Production_hides_openapi_and_swagger()
    {
        using var production = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:SqlServer",
                "Server=localhost;Database=FoundationTests;Integrated Security=True");
        });
        using var client = production.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
    }

    [Fact]
    public void Persistence_resolves_scoped_sql_server_context_and_generates_migration_script()
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<FoundationDbContext>();
        Assert.Same(context, scope.ServiceProvider.GetRequiredService<FoundationDbContext>());
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", context.Database.ProviderName);
        Assert.Empty(context.Model.GetEntityTypes());
        Assert.Single(context.Database.GetMigrations());
        Assert.Contains("__EFMigrationsHistory", context.GetService<IMigrator>().GenerateScript());
    }

    [Fact]
    public void Missing_connection_string_is_rejected_without_exposing_secrets()
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddFoundationInfrastructure(new ConfigurationBuilder().Build()));
        Assert.Equal("ConnectionStrings:SqlServer must be configured.", exception.Message);
    }

    [Fact]
    public void Invalid_database_options_are_rejected()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlServer"] = "Server=localhost;Database=Test;Integrated Security=True",
            ["Database:CommandTimeoutSeconds"] = "0"
        }).Build();
        var services = new ServiceCollection().AddFoundationInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<DatabaseOptions>>().Value);
    }
}
