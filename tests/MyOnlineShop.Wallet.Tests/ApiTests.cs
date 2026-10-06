using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Wallet.Contracts;
using Xunit;

namespace MyOnlineShop.Wallet.Tests;

public sealed class WalletApiFactory : WebApplicationFactory<Program>
{
    internal IdentityReferences Identity { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:SqlServer", "Server=localhost;Database=WalletOffline;Integrated Security=True");
        builder.UseSetting("IdentitySecurity:SigningKeyBase64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.ConfigureServices(services =>
        {
            MemoryWallet.Configure(services, Identity);
            services.AddAuthentication(options =>
            { options.DefaultAuthenticateScheme = "WalletTest"; options.DefaultChallengeScheme = "WalletTest"; options.DefaultForbidScheme = "WalletTest"; })
                .AddScheme<AuthenticationSchemeOptions, WalletAuthentication>("WalletTest", _ => { });
        });
    }
    public HttpClient Client(Guid? user = null, params string[] permissions)
    {
        var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        if (user is not null) client.DefaultRequestHeaders.Add("X-Test-User", user.ToString());
        if (permissions.Length > 0) client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(",", permissions));
        return client;
    }
}
internal sealed class WalletAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (user.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("sub", user) };
        claims.AddRange(Request.Headers["X-Test-Permissions"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => new Claim("permission", value)));
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
public sealed class ApiTests
{
    [Fact]
    public async Task Only_permission_protected_admin_credit_is_exposed_and_server_controls_financial_fields()
    {
        using var factory = new WalletApiFactory(); var owner = factory.Identity.User; var actor = Guid.NewGuid();
        using var anonymous = factory.Client(); using var customer = factory.Client(owner);
        using var admin = factory.Client(actor, IdentityPermissions.CreditWallet);
        var route = $"/api/v1/admin/wallets/{owner}/credit"; var key = Guid.NewGuid();
        var body = new { IdempotencyKey = key, Amount = 500_000m, Currency = "IRR", Description = "Approved adjustment",
            UserId = factory.Identity.Other, ActorId = owner, BalanceBefore = 999, BalanceAfter = 999, TransactionType = "Debit", Status = "Failed" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/wallet")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(route, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync(route, body)).StatusCode);
        admin.DefaultRequestHeaders.Add("X-Correlation-ID", "wallet-api-test");
        using var response = await admin.PostAsJsonAsync(route, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = (await response.Content.ReadFromJsonAsync<ApiResponse<WalletTransactionDto>>())!; var entry = envelope.Data;
        Assert.Equal("wallet-api-test", envelope.CorrelationId); Assert.Equal("wallet-api-test", entry.CorrelationId);
        Assert.Equal(actor, entry.ActorId); Assert.Equal("Credit", entry.Type); Assert.Equal("Posted", entry.Status);
        Assert.Equal(0m, entry.BalanceBefore); Assert.Equal(500_000m, entry.BalanceAfter);
        var json = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("requestFingerprint", json); Assert.DoesNotContain("password", json);
        using var replay = await admin.PostAsJsonAsync(route, body);
        Assert.Equal(entry.Id, (await replay.Content.ReadFromJsonAsync<ApiResponse<WalletTransactionDto>>())!.Data.Id);
        Assert.Equal(500_000m, (await customer.GetFromJsonAsync<ApiResponse<WalletDto>>("/api/v1/wallet"))!.Data.Balance);
        Assert.Single((await customer.GetFromJsonAsync<ApiResponse<WalletTransactionsPage>>("/api/v1/wallet/transactions"))!.Data.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.PostAsJsonAsync("/api/v1/wallet/debit", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.PostAsJsonAsync("/api/v1/wallet/credit", body)).StatusCode);
    }
    [Fact]
    public async Task Owner_reads_ignore_userId_and_errors_use_existing_safe_problem_response()
    {
        using var factory = new WalletApiFactory(); var owner = factory.Identity.User;
        using var admin = factory.Client(Guid.NewGuid(), IdentityPermissions.CreditWallet);
        using var other = factory.Client(factory.Identity.Other); using var client = factory.Client(owner);
        var route = $"/api/v1/admin/wallets/{owner}/credit";
        await admin.PostAsJsonAsync(route, new { IdempotencyKey = Guid.NewGuid(), Amount = 100m, Currency = "IRR", Description = "Adjustment" });
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/wallet?userId={owner}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/wallet/transactions?userId={owner}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/wallet/transactions?pageSize=101")).StatusCode);
        using var badSubject = factory.Client(Guid.Empty);
        Assert.Equal(HttpStatusCode.Unauthorized, (await badSubject.GetAsync("/api/v1/wallet")).StatusCode);
        admin.DefaultRequestHeaders.Add("X-Correlation-ID", "wallet-validation-test");
        using var invalid = await admin.PostAsJsonAsync(route, new { IdempotencyKey = Guid.NewGuid(), Amount = -100m, Currency = "IRR", Description = "private-reason" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType!.MediaType);
        var json = await invalid.Content.ReadAsStringAsync(); Assert.Contains("wallet-validation-test", json); Assert.DoesNotContain("private-reason", json);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/v1/admin/wallets/{Guid.NewGuid()}/credit",
            new { IdempotencyKey = Guid.NewGuid(), Amount = 100m, Currency = "IRR", Description = "Adjustment" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(route, new { Amount = 100m })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/admin/wallets/{Guid.Empty}/credit",
            new { IdempotencyKey = Guid.NewGuid(), Amount = 100m, Currency = "IRR", Description = "Adjustment" })).StatusCode);
    }
}
