using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Infrastructure;
using MyOnlineShop.Catalog.Infrastructure.Persistence;
using MyOnlineShop.Discount.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Domain;
using MyOnlineShop.Pricing.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.PricingDiscount.Tests;

internal sealed class OfflineConnectionRequested : Exception;
internal sealed class BlockConnection : DbConnectionInterceptor
{
    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData data,
        InterceptionResult result, CancellationToken ct = default) => throw new OfflineConnectionRequested();
}
public sealed class ModelTests
{
    [Fact]
    public async Task Catalog_batch_reference_projection_translates_on_SQL_Server_without_opening_connection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:SqlServer"] = "Server=localhost;Database=PricingOffline;Integrated Security=True" }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddFoundationInfrastructure(configuration); services.AddCatalogInfrastructure(configuration);
        services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(new BlockConnection()));
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalogVariantReferences>();
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => catalog.GetManyAsync([Guid.NewGuid(), Guid.NewGuid()], default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => catalog.GetAsync(Guid.NewGuid(), default));
    }
    [Fact]
    public async Task SQL_models_migrations_and_read_projections_are_verified_without_opening_connection()
    {
        const string connection = "Server=localhost;Database=PricingOffline;Integrated Security=True";
        using var prices = new PricingDbContext(new DbContextOptionsBuilder<PricingDbContext>().UseSqlServer(connection).AddInterceptors(new BlockConnection()).Options);
        using var discounts = new DiscountDbContext(new DbContextOptionsBuilder<DiscountDbContext>().UseSqlServer(connection).AddInterceptors(new BlockConnection()).Options);
        foreach (var (context, schema) in new (DbContext, string)[] { (prices, "pricing"), (discounts, "discount") })
        {
            Assert.Single(context.Database.GetMigrations());
            Assert.All(context.Model.GetEntityTypes(), entity => Assert.Equal(schema, entity.GetSchema()));
            var sql = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
            Assert.Contains("decimal(18,4)", sql); Assert.Contains("rowversion", sql);
            Assert.DoesNotContain("DROP TABLE", sql);
            Assert.All(context.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()), key => Assert.Equal(schema, key.PrincipalEntityType.GetSchema()));
        }
        var price = prices.Model.FindEntityType(typeof(VariantPrice))!;
        Assert.Contains(price.GetIndexes(), index => index.IsUnique && index.GetFilter() == "[IsActive] = 1");
        var read = new PriceReadStore(prices); var id = Guid.NewGuid();
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => read.GetCurrentAsync(id, "IRR", FixedClock.Now, default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => read.GetCurrentManyAsync([id], "IRR", FixedClock.Now, default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => read.GetHistoryAsync(id, "IRR", 1, 20, default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => new DiscountReadStore(discounts).GetAsync("IRR", FixedClock.Now, "SALE", default));
        await Assert.ThrowsAsync<OfflineConnectionRequested>(() => new DiscountReadStore(discounts).ListAsync(1, 20, default));
    }
    [Fact]
    public void Existing_modules_keep_their_ownership_boundaries()
    {
        var cart = typeof(MyOnlineShop.Cart.Domain.Cart).Assembly;
        Assert.DoesNotContain(cart.GetReferencedAssemblies(), a => a.Name!.Contains("Pricing") || a.Name.Contains("Discount") || a.Name.Contains("Inventory"));
        var catalog = typeof(MyOnlineShop.Catalog.Domain.Product).Assembly;
        Assert.DoesNotContain(catalog.GetReferencedAssemblies(), a => a.Name!.Contains("Pricing") || a.Name.Contains("Discount"));
        foreach (var assembly in new[] { typeof(VariantPrice).Assembly, typeof(MyOnlineShop.Pricing.Application.PricingCalculation).Assembly,
            typeof(MyOnlineShop.Discount.Domain.DiscountRule).Assembly, typeof(MyOnlineShop.Discount.Application.DiscountCommands).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name!.Contains("Inventory"));
        Assert.DoesNotContain(typeof(MyOnlineShop.Cart.Contracts.CartDto).GetProperties(), p => p.Name.Contains("Price") || p.Name.Contains("Total"));
    }
}
