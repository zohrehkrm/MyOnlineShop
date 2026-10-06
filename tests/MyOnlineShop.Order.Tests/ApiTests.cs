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
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Order.Contracts;
using Xunit;

namespace MyOnlineShop.Order.Tests;

public sealed class OrderApiFactory : WebApplicationFactory<Program>
{
    internal CatalogReferences Catalog { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:SqlServer", "Server=localhost;Database=OrderOffline;Integrated Security=True");
        builder.UseSetting("IdentitySecurity:SigningKeyBase64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.ConfigureServices(services =>
        {
            MemoryModules.Configure(services, Catalog);
            services.AddAuthentication(options =>
            { options.DefaultAuthenticateScheme = "OrderTest"; options.DefaultChallengeScheme = "OrderTest"; options.DefaultForbidScheme = "OrderTest"; })
                .AddScheme<AuthenticationSchemeOptions, OrderAuthentication>("OrderTest", _ => { });
        });
    }
    internal async Task Seed()
    { using var scope = Services.CreateScope(); await MemoryModules.SeedAsync(scope.ServiceProvider, Catalog); }
    public HttpClient Client(Guid? user = null, params string[] permissions)
    {
        var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        if (user is not null) client.DefaultRequestHeaders.Add("X-Test-User", user.ToString());
        if (permissions.Length > 0) client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(",", permissions));
        return client;
    }
}
internal sealed class OrderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (user.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("sub", user) };
        claims.AddRange(Request.Headers["X-Test-Permissions"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("permission", p)));
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
public sealed class ApiTests
{
    [Fact]
    public async Task Checkout_ignores_client_money_and_identity_uses_existing_envelope_and_idempotency()
    {
        using var factory = new OrderApiFactory(); await factory.Seed(); var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        using var client = factory.Client(owner); using var second = factory.Client(other); using var anonymous = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/checkout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/cart/items", new { ProductVariantId = factory.Catalog.Id, Quantity = 2 })).StatusCode);
        var key = Guid.NewGuid(); var body = new { IdempotencyKey = key, Currency = "IRR", UserId = other, ProductPrice = 1, DiscountAmount = 9999999,
            FinalTotal = 1, FinalPayableAmount = 1, InventoryAvailable = 9999999 };
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "checkout-api-test");
        using var response = await client.PostAsJsonAsync("/api/v1/checkout", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.NotNull(response.Headers.Location);
        var envelope = (await response.Content.ReadFromJsonAsync<ApiResponse<OrderDto>>())!; var order = envelope.Data;
        Assert.Equal("checkout-api-test", envelope.CorrelationId); Assert.Equal(2_000_000m, order.PayableAmount);
        Assert.Equal(1_000_000m, order.Items.Single().UnitPrice); Assert.Equal(0m, order.DiscountTotal);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(response.Headers.Location)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync($"/api/v1/orders/{order.Id}?userId={owner}")).StatusCode);
        Assert.Empty((await second.GetFromJsonAsync<ApiResponse<OrderPage>>($"/api/v1/orders?userId={owner}"))!.Data.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await second.PostAsync($"/api/v1/orders/{order.Id}/cancel", null)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ApiResponse<CartDto>>("/api/v1/cart"))!.Data.Items);
        using var replay = await client.PostAsJsonAsync("/api/v1/checkout", body);
        Assert.Equal(order.Id, (await replay.Content.ReadFromJsonAsync<ApiResponse<OrderDto>>())!.Data.Id);
        Assert.Single((await client.GetFromJsonAsync<ApiResponse<OrderPage>>("/api/v1/orders"))!.Data.Items);
        using var conflict = await client.PostAsJsonAsync("/api/v1/checkout", new { IdempotencyKey = key, Currency = "USD" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode); Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType!.MediaType);
        Assert.Contains("checkout-api-test", await conflict.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/orders/{order.Id}/cancel", null)).StatusCode);
    }
    [Fact]
    public async Task Management_requires_permissions_and_cannot_assert_payment()
    {
        using var factory = new OrderApiFactory(); await factory.Seed();
        using var customer = factory.Client(Guid.NewGuid()); using var admin = factory.Client(Guid.NewGuid(), IdentityPermissions.ViewOrders, IdentityPermissions.ManageOrders);
        await customer.PostAsJsonAsync("/api/v1/cart/items", new { ProductVariantId = factory.Catalog.Id, Quantity = 1 });
        var created = await customer.PostAsJsonAsync("/api/v1/checkout", new { IdempotencyKey = Guid.NewGuid(), Currency = "IRR" });
        var id = (await created.Content.ReadFromJsonAsync<ApiResponse<OrderDto>>())!.Data.Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/orders/management")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PatchAsJsonAsync($"/api/v1/orders/management/{id}/status", new { Status = "Failed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/orders/management/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/v1/orders/management/{id}/status", new { Status = "Paid" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/v1/orders/management/{id}/status", new { Status = "Shipped" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/v1/orders/management/{id}/status", new { Status = "Failed" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsync($"/api/v1/orders/{id}/cancel", null)).StatusCode);
        using var badSubject = factory.Client(Guid.Empty);
        Assert.Equal(HttpStatusCode.Unauthorized, (await badSubject.GetAsync("/api/v1/orders")).StatusCode);
    }
}
