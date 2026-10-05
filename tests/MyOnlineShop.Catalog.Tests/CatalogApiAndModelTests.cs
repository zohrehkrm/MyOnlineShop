using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Domain;
using MyOnlineShop.Catalog.Infrastructure.Persistence;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Infrastructure.Persistence;
using Xunit;

namespace MyOnlineShop.Catalog.Tests;

public sealed class CatalogApiAndModelTests
{
    [Fact]
    public async Task Management_requires_permission_and_reuses_response_and_error_contracts()
    {
        using var factory = new CatalogApiFactory();
        using var anonymous = factory.Client();
        using var denied = factory.Client("customer");
        using var manager = factory.Client("manage");
        manager.DefaultRequestHeaders.Add("X-Correlation-ID", "catalog-api-test");
        var input = new CategoryInput { Name = "Clothing", Code = "clothing" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/catalog/categories", input)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.PostAsJsonAsync("/api/v1/catalog/categories", input)).StatusCode);
        using var created = await manager.PostAsJsonAsync("/api/v1/catalog/categories", input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var response = (await created.Content.ReadFromJsonAsync<ApiResponse<CategoryDto>>())!;
        Assert.Equal("catalog-api-test", response.CorrelationId);
        Assert.NotNull(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(created.Headers.Location)).StatusCode);
        using var invalid = await manager.PostAsJsonAsync("/api/v1/catalog/products", new { Name = "Invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType!.MediaType);
        Assert.Contains("catalog-api-test", await invalid.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Product_endpoints_create_variant_publish_update_and_paginate_search()
    {
        using var factory = new CatalogApiFactory();
        using var manager = factory.Client("manage");
        using var anonymous = factory.Client();
        using var categoryResponse = await manager.PostAsJsonAsync("/api/v1/catalog/categories", new CategoryInput { Name = "Grocery", Code = "grocery" });
        var category = (await categoryResponse.Content.ReadFromJsonAsync<ApiResponse<CategoryDto>>())!.Data;
        using var created = await manager.PostAsJsonAsync("/api/v1/catalog/products", new ProductInput { Name = "Coffee", CategoryId = category.Id });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var product = (await created.Content.ReadFromJsonAsync<ApiResponse<ProductDto>>())!.Data;
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/catalog/products/{product.Id}")).StatusCode);
        using var variantResponse = await manager.PostAsJsonAsync($"/api/v1/catalog/products/{product.Id}/variants", new VariantInput { Sku = "coffee-250g" });
        Assert.Equal(HttpStatusCode.Created, variantResponse.StatusCode);
        using var updated = await manager.PutAsJsonAsync($"/api/v1/catalog/products/{product.Id}", new ProductInput
        {
            Name = "Coffee", CategoryId = category.Id, Status = "Active", Description = "Roasted coffee",
            Specifications = [new() { Name = "Net weight", Value = "250", Unit = "g" }],
            Images = [new() { Url = "https://example.test/coffee.png", SortOrder = 0, IsPrimary = true }]
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var found = await anonymous.GetAsync("/api/v1/catalog/products?search=coffee&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        var page = (await found.Content.ReadFromJsonAsync<ApiResponse<PageDto<ProductSummaryDto>>>())!.Data;
        Assert.Equal(product.Id, Assert.Single(page.Items).Id);
        Assert.Equal("https://example.test/coffee.png", page.Items[0].PrimaryImageUrl);
        using var detail = await anonymous.GetAsync($"/api/v1/catalog/products/{product.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.DoesNotContain("rowVersion", await detail.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var invalidPage = await anonymous.GetAsync("/api/v1/catalog/products?pageSize=101");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
    }

    [Fact]
    public async Task Catalog_policy_uses_permission_claims_instead_of_role_name_alone()
    {
        using var factory = new CatalogApiFactory();
        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();
        var roleOnly = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrator")], "Test"));
        var permitted = new ClaimsPrincipal(new ClaimsIdentity([new Claim("permission", IdentityPermissions.ManageCatalog)], "Test"));
        Assert.False((await authorization.AuthorizeAsync(roleOnly, null, IdentityPermissions.ManageCatalog)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(permitted, null, IdentityPermissions.ManageCatalog)).Succeeded);
    }

    [Fact]
    public void Sql_server_model_and_forward_migration_enforce_schema_keys_and_relationships_offline()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer("Server=localhost;Database=CatalogOffline;Integrated Security=True").Options;
        using var context = new CatalogDbContext(options);
        Assert.All(context.Model.GetEntityTypes(), entity => Assert.Equal("catalog", entity.GetSchema()));
        Assert.Single(context.Database.GetMigrations());
        var sql = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Variants_Sku]", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Variants_ProductId_CombinationHash]", sql);
        Assert.Contains("[IsPrimary] = 1", sql);
        Assert.Contains("FOREIGN KEY ([ProductId], [AttributeId], [ValueId])", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
        Assert.DoesNotContain("Price", sql);
        Assert.DoesNotContain("Quantity", sql);
        var variantValues = context.Model.FindEntityType(typeof(VariantAttributeValue))!;
        Assert.Equal(2, variantValues.GetForeignKeys().Count());
        Assert.Contains(context.Model.FindEntityType(typeof(Product))!.GetProperties(), property => property.IsConcurrencyToken);
    }

    [Fact]
    public void Identity_permission_migration_adds_catalog_grant_without_recreating_identity()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlServer("Server=localhost;Database=IdentityOffline;Integrated Security=True").Options;
        using var context = new IdentityDbContext(options);
        var migrations = context.Database.GetMigrations().ToArray();
        Assert.Contains(migrations, value => value.EndsWith("_AddCatalogPermission", StringComparison.Ordinal));
        var sql = context.GetService<IMigrator>().GenerateScript(migrations[0], migrations[1]);
        Assert.Contains("catalog.manage", sql);
        Assert.Contains("[identity].[RolePermissions]", sql);
        Assert.DoesNotContain("CREATE TABLE", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
    }
}
