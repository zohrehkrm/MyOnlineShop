using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using MyOnlineShop.Identity.Contracts;
using Xunit;

namespace MyOnlineShop.Identity.Tests;

public sealed partial class IdentityHttpTests
{
    [Fact]
    public async Task Real_customer_JWT_cannot_access_administrative_APIs_across_modules()
    {
        using var factory = new IdentityHttpFactory(); using var anonymous = factory.Client();
        await RegisterAsync(anonymous); var tokens = await LoginAsync(anonymous);
        using var customer = factory.Client(tokens.AccessToken);
        foreach (var route in new[] { "/api/v1/catalog/manage/products", "/api/v1/inventory/warehouses", "/api/v1/inventory/adjustments",
            "/api/v1/orders/management", "/api/v1/shipping/management/shipments", "/api/v1/discounts", "/api/v1/reports/wallet" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(route)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync($"/api/v1/admin/wallets/{Guid.NewGuid()}/credit", null)).StatusCode);
    }

    [Fact]
    public async Task Signed_token_role_and_permission_claims_cannot_override_persisted_assignments()
    {
        using var factory = new IdentityHttpFactory(); using var anonymous = factory.Client(); await RegisterAsync(anonymous);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken((await LoginAsync(anonymous)).AccessToken);
        var claims = jwt.Claims.Where(c => c.Type is not ("aud" or "iss" or "exp" or "nbf" or "role" or "permission"))
            .Concat(new[] { new Claim("role", IdentityPermissions.AdministratorRole), new Claim("permission", IdentityPermissions.ManageCatalog) });
        using var client = factory.Client(Sign(factory, claims));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/identity/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/identity-probe/administrator")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/catalog/manage/products")).StatusCode);
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("sid")]
    [InlineData("stamp")]
    [InlineData("jti")]
    [InlineData("iat")]
    public async Task Duplicate_required_claims_are_rejected(string claimType)
    {
        using var factory = new IdentityHttpFactory(); using var anonymous = factory.Client(); await RegisterAsync(anonymous);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken((await LoginAsync(anonymous)).AccessToken);
        var claims = jwt.Claims.Where(c => c.Type is not ("aud" or "iss" or "exp" or "nbf"));
        using var client = factory.Client(Sign(factory, claims.Append(new Claim(claimType, jwt.Claims.First(c => c.Type == claimType).Value))));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/identity/me")).StatusCode);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("eyJhbGciOiJub25lIn0.e30.")]
    public async Task Malformed_or_unsigned_tokens_return_safe_correlated_errors(string token)
    {
        using var factory = new IdentityHttpFactory(); using var client = factory.Client(token);
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "invalid-jwt");
        using var response = await client.GetAsync("/api/v1/identity/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(); Assert.Contains("invalid-jwt", body);
        Assert.DoesNotContain(token, body); Assert.DoesNotContain("SecurityToken", body);
        Assert.DoesNotContain(token, string.Join('\n', factory.Logs.Messages));
    }

    private static string Sign(IdentityHttpFactory factory, IEnumerable<Claim> claims) => new JwtSecurityTokenHandler().WriteToken(
        new JwtSecurityToken("MyOnlineShop", "MyOnlineShop.Api", claims, DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(factory.Key)), SecurityAlgorithms.HmacSha256)));
}
