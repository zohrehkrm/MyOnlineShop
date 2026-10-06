using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Order.Domain;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Shipping.Contracts;
using Xunit;

namespace MyOnlineShop.Shipping.Tests;

public sealed class ShippingApiFactory : WebApplicationFactory<Program>
{
    internal MyOnlineShop.Order.Tests.CatalogReferences Catalog { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); builder.UseSetting("ConnectionStrings:SqlServer", "Server=localhost;Database=ShippingOffline;Integrated Security=True");
        builder.UseSetting("IdentitySecurity:SigningKeyBase64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.ConfigureServices(services =>
        {
            MyOnlineShop.Order.Tests.MemoryModules.Configure(services, Catalog); MemoryShipping.Configure(services);
            services.AddAuthentication(options => { options.DefaultAuthenticateScheme = "ShippingTest"; options.DefaultChallengeScheme = "ShippingTest"; options.DefaultForbidScheme = "ShippingTest"; })
                .AddScheme<AuthenticationSchemeOptions, ShippingAuthentication>("ShippingTest", _ => { });
        });
    }
    internal async Task Seed() { using var scope = Services.CreateScope(); await MyOnlineShop.Order.Tests.MemoryModules.SeedAsync(scope.ServiceProvider, Catalog); }
    internal async Task Paid(Guid id)
    {
        using var scope = Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var order = await db.Orders.SingleAsync(value => value.Id == id);
        order.Transition(OrderStatus.Paid, MyOnlineShop.Order.Tests.FixedClock.Now); order.Transition(OrderStatus.Processing, MyOnlineShop.Order.Tests.FixedClock.Now);
        await db.SaveChangesAsync(); // Trusted test fixture only; does not implement or verify Payment.
    }
    public HttpClient Client(Guid? user = null, params string[] permissions)
    {
        var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        if (user is not null) client.DefaultRequestHeaders.Add("X-Test-User", user.ToString());
        if (permissions.Length > 0) client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(',', permissions)); return client;
    }
}
internal sealed class ShippingAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString(); if (user.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("sub", user), new("role", "Administrator") }; // Role alone must not bypass permission policies.
        claims.AddRange(Request.Headers["X-Test-Permissions"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => new Claim("permission", value)));
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
public sealed class ApiTests
{
    [Fact]
    public async Task Shipping_api_reuses_authorization_envelopes_and_rejects_arbitrary_customer_management()
    {
        using var factory = new ShippingApiFactory(); using var anonymous = factory.Client(); using var customer = factory.Client(Guid.NewGuid());
        using var admin = factory.Client(Guid.NewGuid(), IdentityPermissions.ManageShippingMethods);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/shipping/methods")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync("/api/v1/shipping/management/methods", Harness.Method())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/shipping/management/methods")).StatusCode);
        var created = await admin.PostAsJsonAsync("/api/v1/shipping/management/methods", Harness.Method()); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var method = (await created.Content.ReadFromJsonAsync<ApiResponse<ShippingMethodDto>>())!.Data;
        Assert.Equal(method.Id, Assert.Single((await customer.GetFromJsonAsync<ApiResponse<ShippingMethodDto[]>>("/api/v1/shipping/methods"))!.Data).Id);
        var quote = await customer.PostAsJsonAsync("/api/v1/shipping/quotes", new { ShippingMethodId = method.Id, Currency = "IRR", Address = Harness.Address(), ShippingCost = 1, Cost = 1 });
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode); Assert.Equal(50_000m, (await quote.Content.ReadFromJsonAsync<ApiResponse<ShippingQuoteSnapshot>>())!.Data.Cost);
        var invalid = await customer.PostAsJsonAsync("/api/v1/shipping/quotes", new { ShippingMethodId = method.Id, Currency = "IRR", Address = new { Recipient = "Name" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType!.MediaType);
        using var emptyUser = factory.Client(Guid.Empty); Assert.Equal(HttpStatusCode.Unauthorized, (await emptyUser.GetAsync("/api/v1/shipping/methods")).StatusCode);
    }
    [Fact]
    public async Task Checkout_and_shipment_api_preserve_server_money_owner_address_tracking_and_permissions()
    {
        using var factory = new ShippingApiFactory(); await factory.Seed(); var owner = Guid.NewGuid();
        using var customer = factory.Client(owner); using var other = factory.Client(Guid.NewGuid());
        using var admin = factory.Client(Guid.NewGuid(), IdentityPermissions.ManageShippingMethods, IdentityPermissions.ManageShipments, IdentityPermissions.ViewShipments);
        var methodResponse = await admin.PostAsJsonAsync("/api/v1/shipping/management/methods", Harness.Method());
        var method = (await methodResponse.Content.ReadFromJsonAsync<ApiResponse<ShippingMethodDto>>())!.Data;
        await customer.PostAsJsonAsync("/api/v1/cart/items", new { ProductVariantId = factory.Catalog.Id, Quantity = 2 });
        customer.DefaultRequestHeaders.Add("X-Correlation-ID", "shipping-api");
        var orderResponse = await customer.PostAsJsonAsync("/api/v1/checkout", new { IdempotencyKey = Guid.NewGuid(), Currency = "IRR",
            ShippingCost = 1, UserId = Guid.NewGuid(), Shipping = new { ShippingMethodId = method.Id, Address = Harness.Address(), Cost = 1 } });
        Assert.Equal(HttpStatusCode.Created, orderResponse.StatusCode); var order = (await orderResponse.Content.ReadFromJsonAsync<ApiResponse<OrderDto>>())!.Data;
        Assert.Equal(2_050_000m, order.PayableAmount); Assert.Equal(50_000m, order.ShippingCost);
        var route = $"/api/v1/shipping/management/orders/{order.Id}/shipment";
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync(route, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync(route, null)).StatusCode);
        await factory.Paid(order.Id);
        var created = await admin.PostAsJsonAsync(route, new { ShippingCost = 1, UserId = Guid.NewGuid(), Address = Harness.Address("Changed") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var shipment = (await created.Content.ReadFromJsonAsync<ApiResponse<ShipmentDto>>())!.Data;
        Assert.Equal("Original Recipient", shipment.Address.Recipient); Assert.Equal(50_000m, shipment.ShippingCost);
        var own = await customer.GetFromJsonAsync<ApiResponse<ShipmentDto>>($"/api/v1/shipping/orders/{order.Id}/shipment"); Assert.Equal("shipping-api", own!.CorrelationId);
        Assert.Equal(shipment.Id, own.Data.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/shipping/orders/{order.Id}/shipment?userId={owner}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/v1/shipping/management/shipments/{shipment.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PatchAsJsonAsync($"/api/v1/shipping/management/shipments/{shipment.Id}/status", new { ExpectedRevision = shipment.Revision, Status = "Preparing" })).StatusCode);
        var preparingResponse = await admin.PatchAsJsonAsync($"/api/v1/shipping/management/shipments/{shipment.Id}/status", new { ExpectedRevision = shipment.Revision, Status = "Preparing" });
        Assert.Equal(HttpStatusCode.OK, preparingResponse.StatusCode); var preparing = (await preparingResponse.Content.ReadFromJsonAsync<ApiResponse<ShipmentDto>>())!.Data;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/v1/shipping/management/shipments/{shipment.Id}/status", new { ExpectedRevision = shipment.Revision, Status = "Preparing" })).StatusCode);
        var tracked = await admin.PutAsJsonAsync($"/api/v1/shipping/management/shipments/{shipment.Id}/tracking", new { ExpectedRevision = preparing.Revision, TrackingNumber = "API-TRACK", Carrier = "Carrier" });
        Assert.Equal(HttpStatusCode.OK, tracked.StatusCode); Assert.Equal("API-TRACK", (await tracked.Content.ReadFromJsonAsync<ApiResponse<ShipmentDto>>())!.Data.TrackingNumber);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(created.Headers.Location)).StatusCode);
    }
}
