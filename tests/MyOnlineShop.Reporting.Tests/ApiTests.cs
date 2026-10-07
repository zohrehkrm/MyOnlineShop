using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Reporting.Contracts;
using Xunit;

namespace MyOnlineShop.Reporting.Tests;

internal sealed class ReportsApiFactory : WebApplicationFactory<Program>
{
    internal Harness Reports { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); builder.UseSetting("ConnectionStrings:SqlServer", "Server=localhost;Database=ReportingOffline;Integrated Security=True");
        builder.UseSetting("IdentitySecurity:SigningKeyBase64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.UseSetting("BootstrapIdentityAdmin", "false"); builder.UseSetting("Messaging:Enabled", "false");
        builder.UseSetting("RefundProcessing:Enabled", "false"); builder.UseSetting("Redis:Enabled", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IReportingQueries>(); services.AddScoped<IReportingQueries>(_ => Reports.Queries);
            services.AddAuthentication(options => { options.DefaultAuthenticateScheme = "ReportsTest"; options.DefaultChallengeScheme = "ReportsTest"; options.DefaultForbidScheme = "ReportsTest"; })
                .AddScheme<AuthenticationSchemeOptions, ReportsAuthentication>("ReportsTest", _ => { });
        });
    }
    internal HttpClient Client(bool authenticated, params string[] permissions)
    {
        var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        if (authenticated) client.DefaultRequestHeaders.Add("X-Test-User", Guid.NewGuid().ToString());
        if (permissions.Length > 0) client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(',', permissions));
        return client;
    }
    protected override void Dispose(bool disposing) { if (disposing) Reports.Dispose(); base.Dispose(disposing); }
}
internal sealed class ReportsAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString(); if (user.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("sub", user), new("role", "Administrator") }; // Role names alone never grant report permissions.
        claims.AddRange(Request.Headers["X-Test-Permissions"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => new Claim("permission", x)));
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
public sealed class ApiTests
{
    private const string Dates = "?fromUtc=2026-10-01T00:00:00Z&toUtc=2026-10-05T00:00:00Z";
    [Theory]
    [InlineData("dashboard")] [InlineData("sales")] [InlineData("orders")] [InlineData("products")]
    [InlineData("inventory")] [InlineData("customers")] [InlineData("wallet")] [InlineData("shipping")]
    public async Task All_reports_require_authentication_and_explicit_permissions(string report)
    {
        using var factory = new ReportsApiFactory(); using var anonymous = factory.Client(false); using var roleOnly = factory.Client(true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/reports/" + report)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await roleOnly.GetAsync("/api/v1/reports/" + report)).StatusCode);
    }
    [Fact]
    public async Task Scoped_permissions_do_not_grant_financial_customer_or_combined_dashboard_access()
    {
        using var factory = new ReportsApiFactory(); using var sales = factory.Client(true, IdentityPermissions.ViewReports, IdentityPermissions.ReportSales);
        Assert.Equal(HttpStatusCode.OK, (await sales.GetAsync("/api/v1/reports/sales" + Dates)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/v1/reports/wallet")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/v1/reports/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/v1/reports/dashboard")).StatusCode);
        using var financialOnly = factory.Client(true, IdentityPermissions.ReportFinancial);
        Assert.Equal(HttpStatusCode.Forbidden, (await financialOnly.GetAsync("/api/v1/reports/wallet")).StatusCode);
    }
    [Fact]
    public async Task Authorized_reports_reuse_envelopes_correlation_validation_and_bounded_data()
    {
        using var factory = new ReportsApiFactory(); await factory.Reports.SeedAsync();
        using var client = factory.Client(true, IdentityPermissions.ViewReports, IdentityPermissions.ReportSales, IdentityPermissions.ReportInventory,
            IdentityPermissions.ReportCustomers, IdentityPermissions.ReportFinancial, IdentityPermissions.ViewShipments);
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "report-api-test");
        var dashboard = (await client.GetFromJsonAsync<ApiResponse<DashboardDto>>("/api/v1/reports/dashboard" + Dates))!;
        Assert.Equal("report-api-test", dashboard.CorrelationId); Assert.Equal(5, dashboard.Data.OrderStatuses.Sum(x => x.Count));
        foreach (var report in new[] { "sales", "orders", "products", "inventory", "customers", "wallet", "shipping" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/reports/" + report + Dates + "&pageSize=1")).StatusCode);
        var orders = (await client.GetFromJsonAsync<ApiResponse<OrderReportDto>>("/api/v1/reports/orders" + Dates + "&pageSize=1"))!.Data;
        Assert.Single(orders.Orders.Items); Assert.Equal(5, orders.Orders.TotalCount);
        var bad = await client.GetAsync("/api/v1/reports/orders" + Dates + "&pageSize=101"); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/reports/payments")).StatusCode);
    }
}
