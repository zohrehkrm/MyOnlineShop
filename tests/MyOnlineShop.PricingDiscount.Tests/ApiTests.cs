using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Discount.Contracts;
using MyOnlineShop.Discount.Infrastructure.Persistence;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Pricing.Presentation;
using Xunit;

namespace MyOnlineShop.PricingDiscount.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task Discount_management_validates_product_and_category_references()
    {
        using var factory = new PricingApiFactory();
        using var admin = factory.Client(Guid.NewGuid(), IdentityPermissions.ManageDiscount);
        foreach (var (product, category, expected) in new (Guid?, Guid?, HttpStatusCode)[]
        {
            (factory.Catalog.ProductId, null, HttpStatusCode.Created), (null, factory.Catalog.CategoryId, HttpStatusCode.Created),
            (Guid.NewGuid(), null, HttpStatusCode.NotFound), (null, Guid.NewGuid(), HttpStatusCode.NotFound),
            (factory.Catalog.ProductId, factory.Catalog.CategoryId, HttpStatusCode.BadRequest)
        })
        {
            using var result = await admin.PostAsJsonAsync("/api/v1/discounts", new DiscountInput
            {
                Name = "Targeted", Currency = "IRR", Value = 20, StartsAtUtc = FixedClock.Now.AddDays(-1), EndsAtUtc = FixedClock.Now.AddDays(1),
                ProductId = product, CategoryId = category
            });
            Assert.Equal(expected, result.StatusCode);
        }
    }
    private static PriceInput Price(Guid variant, decimal amount) => new() { ProductVariantId = variant, BasePrice = amount, Currency = "IRR", EffectiveFromUtc = FixedClock.Now.AddDays(-1) };
    private static DiscountInput Discount(Guid? variant = null, decimal value = 20, bool active = true) =>
        new() { Name = "Offer", Type = "Percentage", Value = value, Currency = "IRR", IsActive = active,
            StartsAtUtc = FixedClock.Now.AddDays(-1), EndsAtUtc = FixedClock.Now.AddDays(1), ProductVariantId = variant };
    [Fact]
    public async Task Permissions_status_validation_and_discount_commands_reuse_existing_pipeline()
    {
        using var factory = new PricingApiFactory(); using var anonymous = factory.Client(); using var customer = factory.Client(Guid.NewGuid());
        using var admin = factory.Client(Guid.NewGuid(), IdentityPermissions.ManagePricing, IdentityPermissions.ManageDiscount);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/pricing/variants?currency=IRR")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync("/api/v1/pricing/prices", Price(factory.Catalog.Id, 100))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/discounts")).StatusCode);
        using var created = await admin.PostAsJsonAsync("/api/v1/discounts", Discount(factory.Catalog.Id));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.NotNull(created.Headers.Location);
        var dto = (await created.Content.ReadFromJsonAsync<ApiResponse<DiscountRuleDto>>())!.Data;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/discounts/{dto.Id}", Discount(factory.Catalog.Id, 25))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/v1/discounts/{dto.Id}/status", new { IsActive = false })).StatusCode);
        using var read = await admin.GetAsync($"/api/v1/discounts/{dto.Id}");
        var updated = (await read.Content.ReadFromJsonAsync<ApiResponse<DiscountRuleDto>>())!.Data;
        Assert.False(updated.IsActive); Assert.Equal(25m, updated.Value);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/discounts", Discount(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/discounts", Discount(value: 101))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/v1/discounts/{dto.Id}/status", new { })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var rule = scope.ServiceProvider.GetRequiredService<DiscountDbContext>().Rules.Single();
        Assert.NotEqual(Guid.Empty, rule.UpdatedBy); Assert.Equal(FixedClock.Now, rule.UpdatedAtUtc);
    }
    [Fact]
    public async Task Preview_ignores_client_money_and_cart_reloads_current_price_using_authenticated_owner()
    {
        using var factory = new PricingApiFactory(); var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        using var admin = factory.Client(Guid.NewGuid(), IdentityPermissions.ManagePricing, IdentityPermissions.ManageDiscount);
        using var customer = factory.Client(owner); using var second = factory.Client(other);
        var created = await admin.PostAsJsonAsync("/api/v1/pricing/prices", Price(factory.Catalog.Id, 1_000_000m));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var price = (await created.Content.ReadFromJsonAsync<ApiResponse<PriceDto>>())!.Data;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/discounts", Discount())).StatusCode);
        customer.DefaultRequestHeaders.Add("X-Correlation-ID", "pricing-api-test");
        using var preview = await customer.PostAsJsonAsync("/api/v1/pricing/preview", new
        { ProductVariantId = factory.Catalog.Id, Quantity = 2, Currency = "IRR", BasePrice = 1, FinalPrice = 1, DiscountAmount = 0, FinalPayableAmount = 2 });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var quote = (await preview.Content.ReadFromJsonAsync<ApiResponse<PricingQuote>>())!;
        Assert.Equal("pricing-api-test", quote.CorrelationId); Assert.Equal(800_000m, quote.Data.Lines.Single().FinalUnitPrice);
        Assert.Equal(1_600_000m, quote.Data.Lines.Single().TotalLineAmount);
        var added = await customer.PostAsJsonAsync("/api/v1/cart/items", new { ProductVariantId = factory.Catalog.Id, Quantity = 2, UserId = other });
        var cart = (await added.Content.ReadFromJsonAsync<ApiResponse<CartDto>>())!.Data;
        var updated = await customer.PutAsJsonAsync($"/api/v1/cart/items/{cart.Items.Single().Id}", new { Quantity = 4 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        async Task<CartPricingPreview> CartQuote(HttpClient client) =>
            (await client.GetFromJsonAsync<ApiResponse<CartPricingPreview>>($"/api/v1/cart/pricing?currency=IRR&userId={owner}"))!.Data;
        Assert.Empty((await CartQuote(second)).Quote.Lines);
        Assert.Equal(3_200_000m, (await CartQuote(customer)).Quote.Lines.Single().TotalLineAmount);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/pricing/prices/{price.Id}", Price(factory.Catalog.Id, 2_000_000m))).StatusCode);
        Assert.Equal(6_400_000m, (await CartQuote(customer)).Quote.Lines.Single().TotalLineAmount);
        var rawCart = await customer.GetStringAsync("/api/v1/cart");
        Assert.DoesNotContain("price", rawCart, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("discount", rawCart, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync($"/api/v1/pricing/variants/{factory.Catalog.Id}/discounts?currency=IRR")).StatusCode);
        using var invalid = await customer.PostAsJsonAsync("/api/v1/pricing/preview", new { ProductVariantId = factory.Catalog.Id, Quantity = 0, Currency = "IRR" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType!.MediaType);
        Assert.Contains("pricing-api-test", await invalid.Content.ReadAsStringAsync());
    }
}
