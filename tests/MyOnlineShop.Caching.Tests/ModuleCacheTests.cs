using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Domain;
using MyOnlineShop.Catalog.Infrastructure;
using MyOnlineShop.Catalog.Infrastructure.Persistence;
using MyOnlineShop.Discount.Contracts;
using MyOnlineShop.Discount.Domain;
using MyOnlineShop.Discount.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Pricing.Domain;
using MyOnlineShop.Pricing.Infrastructure.Persistence;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Shipping.Application;
using MyOnlineShop.Shipping.Contracts;
using MyOnlineShop.Shipping.Domain;
using MyOnlineShop.Shipping.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Caching.Tests;

public sealed class ModuleCacheTests
{
    [Fact]
    public async Task Catalog_category_brand_details_use_dto_cache_and_visibility_isolated()
    {
        var h = new Harness();
        await using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        var category = Category.Create("Category", "CATEGORY", null, true); var brand = Brand.Create("Brand", "BRAND", true);
        var product = Product.Create("Product", "Description", category.Id, brand.Id, ProductKind.Physical, h.Clock.GetUtcNow());
        product.AddVariant("SKU-1", true, null, null, [], h.Clock.GetUtcNow());
        product.ReplaceDetails([new("https://example.com/image.png", "Image", 0, true)], [new("Material", "Generic", null, 0)], new Dictionary<string,string> { ["Origin"] = "Test" });
        db.Categories.Add(category); db.Brands.Add(brand); db.Products.Add(product); await db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddCatalogInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:SqlServer"] = "unused" }).Build());
        services.RemoveAll<CatalogDbContext>(); services.AddSingleton(db); services.AddSingleton(h.Read);
        await using var provider = services.BuildServiceProvider();
        var queries = provider.GetRequiredService<ICatalogQueries>();
        Assert.Equal(category.Id, (await queries.GetCategoryAsync(category.Id, false, default)).Id);
        Assert.Equal(brand.Id, (await queries.GetBrandAsync(brand.Id, false, default)).Id);
        var productDto = await queries.GetProductAsync(product.Id, true, default);
        Assert.Equal("SKU-1", Assert.Single(productDto.Variants).Sku);
        product.Update("Updated product", product.Description, category.Id, brand.Id, ProductKind.Physical, ProductStatus.Draft, null, null, h.Clock.GetUtcNow());
        await db.SaveChangesAsync();
        Assert.Equal("SKU-1", Assert.Single((await queries.GetProductAsync(product.Id, true, default)).Variants).Sku);
        Assert.Equal("Product", (await queries.GetProductAsync(product.Id, true, default)).Name);
        category.Update("Category", "CATEGORY", null, false); brand.Update("Brand", "BRAND", false); await db.SaveChangesAsync();
        Assert.Equal(category.Id, (await queries.GetCategoryAsync(category.Id, false, default)).Id);
        Assert.False((await queries.GetCategoryAsync(category.Id, true, default)).IsActive);
        // Exercise the production commit/invalidation hook; InMemory proves control flow, not SQL transactions.
        var unit = provider.GetRequiredService<MyOnlineShop.Catalog.Application.ICatalogUnitOfWork>();
        var before = await h.Cache.GetAsync<string>(MyOnlineShop.BuildingBlocks.Abstractions.CacheKeys.Generation("catalog"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.ExecuteAsync<string>(_ => throw new InvalidOperationException("failed command"), default));
        Assert.Equal(before, await h.Cache.GetAsync<string>(MyOnlineShop.BuildingBlocks.Abstractions.CacheKeys.Generation("catalog"), default));
        await unit.ExecuteAsync(_ => Task.FromResult("successful command"), default);
        Assert.NotEqual(before, await h.Cache.GetAsync<string>(MyOnlineShop.BuildingBlocks.Abstractions.CacheKeys.Generation("catalog"), default));
        var refreshed = await queries.GetProductAsync(product.Id, true, default);
        Assert.Equal("Updated product", refreshed.Name); Assert.Single(refreshed.Images); Assert.Single(refreshed.Specifications);
        await Assert.ThrowsAsync<MyOnlineShop.Catalog.Application.CatalogException>(() => queries.GetCategoryAsync(category.Id, false, default));
        await Assert.ThrowsAsync<MyOnlineShop.Catalog.Application.CatalogException>(() => queries.GetBrandAsync(brand.Id, false, default));
    }
    [Fact]
    public async Task Price_record_can_be_cached_but_current_prices_always_read_database()
    {
        var h = new Harness(); await using var db = new PricingDbContext(new DbContextOptionsBuilder<PricingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var price = VariantPrice.Create(new() { ProductVariantId = Guid.NewGuid(), BasePrice = 100m, Currency = "IRR", EffectiveFromUtc = h.Clock.GetUtcNow().AddDays(-1) });
        db.Prices.Add(price); await db.SaveChangesAsync();
        var queries = new PriceReadStore(db, h.Read);
        Assert.True((await queries.GetAsync(price.Id, default)).IsActive);
        price.SetActive(false); await db.SaveChangesAsync();
        Assert.True((await queries.GetAsync(price.Id, default)).IsActive);
        Assert.Null(await queries.GetCurrentAsync(price.ProductVariantId, "IRR", h.Clock.GetUtcNow(), default));
        Assert.Empty(await queries.GetCurrentManyAsync([price.ProductVariantId], "IRR", h.Clock.GetUtcNow(), default));
        await h.Read.InvalidateAsync("pricing"); Assert.False((await queries.GetAsync(price.Id, default)).IsActive);
    }
    [Fact]
    public async Task Discount_lookup_is_cached_but_eligibility_candidates_remain_authoritative()
    {
        var h = new Harness(); await using var db = new DiscountDbContext(new DbContextOptionsBuilder<DiscountDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var actor = Guid.NewGuid(); var rule = DiscountRule.Create(new() { Name = "Sale", Value = 10m, Currency = "IRR", StartsAtUtc = h.Clock.GetUtcNow().AddDays(-1), EndsAtUtc = h.Clock.GetUtcNow().AddDays(1) }, actor, h.Clock.GetUtcNow());
        db.Rules.Add(rule); await db.SaveChangesAsync(); var queries = new DiscountReadStore(db, h.Read);
        Assert.True((await queries.GetAsync(rule.Id, default)).IsActive);
        rule.SetActive(false, actor, h.Clock.GetUtcNow()); await db.SaveChangesAsync();
        Assert.True((await queries.GetAsync(rule.Id, default)).IsActive);
        Assert.Empty(await queries.GetAsync("IRR", h.Clock.GetUtcNow(), null, default));
        await h.Read.InvalidateAsync("discount"); Assert.False((await queries.GetAsync(rule.Id, default)).IsActive);
    }
    [Fact]
    public async Task Shipping_list_is_cached_by_currency_but_final_quote_uses_current_database_method()
    {
        var h = new Harness(); await using var db = new ShippingDbContext(new DbContextOptionsBuilder<ShippingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        ShippingMethodInput Input(decimal cost) => new() { Name = "Standard", Code = "STANDARD", BaseCost = cost, Currency = "IRR", IsActive = true };
        var method = ShippingMethod.Create(Input(100m)); db.Methods.Add(method); await db.SaveChangesAsync();
        var queries = new ShippingQueries(db, new NoOrders(), h.Read); var quotes = new ShippingQuotes(new ShippingStore(db), h.Clock);
        Assert.Equal(100m, Assert.Single(await queries.AvailableMethodsAsync("irr", default)).BaseCost);
        method.Update(Input(200m)); await db.SaveChangesAsync();
        Assert.Equal(100m, Assert.Single(await queries.AvailableMethodsAsync("IRR", default)).BaseCost);
        Assert.Empty(await queries.AvailableMethodsAsync("USD", default));
        var quote = await quotes.CalculateAsync(new() { ShippingMethodId = method.Id, Currency = "IRR", Address = new()
        { Recipient = "Name", PhoneNumber = "+989123456789", State = "State", City = "City", Street = "Street", PostalCode = "12345", CountryCode = "IR" } }, default);
        Assert.Equal(200m, quote.Cost);
        await h.Read.InvalidateAsync("shipping"); Assert.Equal(200m, Assert.Single(await queries.AvailableMethodsAsync("IRR", default)).BaseCost);
        Assert.DoesNotContain(h.Backend.Entries.Keys, key => key.Contains("quote") || key.Contains("Name"));
    }
    private sealed class NoOrders : IOrderShippingSnapshots
    {
        public Task<OrderShippingSnapshot?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult<OrderShippingSnapshot?>(null);
    }
}
