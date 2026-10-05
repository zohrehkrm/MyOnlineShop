using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Catalog.Application;
using MyOnlineShop.Catalog.Contracts;
using Xunit;

namespace MyOnlineShop.Catalog.Tests;

public sealed class CatalogApplicationTests
{
    [Fact]
    public async Task Variant_reference_contract_reports_catalog_owned_kind_and_activation()
    {
        using var harness = new CatalogHarness();
        var commands = harness.Services.GetRequiredService<ICatalogCommands>();
        var references = harness.Services.GetRequiredService<ICatalogVariantReferences>();
        var category = await commands.CreateCategoryAsync(new() { Name = "Digital", Code = "digital" }, default);
        var product = await commands.CreateProductAsync(new() { Name = "Download", CategoryId = category.Id, Kind = "Digital" }, default);
        var variant = await commands.AddVariantAsync(product.Id, new() { Sku = "download-base" }, default);
        var draft = await references.GetAsync(variant.Id, default);
        Assert.NotNull(draft); Assert.Equal("Digital", draft.ProductKind); Assert.False(draft.IsActive);
        await commands.UpdateProductAsync(product.Id, new() { Name = "Download", CategoryId = category.Id, Kind = "Digital", Status = "Active" }, default);
        Assert.True((await references.GetAsync(variant.Id, default))!.IsActive);
        await commands.UpdateCategoryAsync(category.Id, new() { Name = "Digital", Code = "digital", IsActive = false }, default);
        Assert.False((await references.GetAsync(variant.Id, default))!.IsActive);
        Assert.Null(await references.GetAsync(Guid.NewGuid(), default));
    }
    [Fact]
    public async Task Category_commands_reject_cycles_inactive_parents_and_invalid_references()
    {
        using var harness = new CatalogHarness();
        var commands = harness.Services.GetRequiredService<ICatalogCommands>();
        var root = await commands.CreateCategoryAsync(new() { Name = "Root", Code = "root" }, default);
        var child = await commands.CreateCategoryAsync(new() { Name = "Child", Code = "child", ParentId = root.Id }, default);
        await Assert.ThrowsAsync<CatalogException>(() => commands.UpdateCategoryAsync(root.Id, new() { Name = root.Name, Code = root.Code, ParentId = child.Id }, default));
        await Assert.ThrowsAsync<CatalogException>(() => commands.UpdateCategoryAsync(root.Id, new() { Name = root.Name, Code = root.Code, IsActive = false }, default));
        await Assert.ThrowsAsync<CatalogException>(() => commands.CreateCategoryAsync(new() { Name = "Invalid", Code = "invalid", ParentId = Guid.NewGuid() }, default));
        var inactive = await commands.CreateCategoryAsync(new() { Name = "Inactive", Code = "inactive", IsActive = false }, default);
        await Assert.ThrowsAsync<CatalogException>(() => commands.CreateCategoryAsync(new() { Name = "Invalid", Code = "invalid", ParentId = inactive.Id }, default));
    }
    [Fact]
    public async Task Product_creation_and_update_preserve_variants_and_generic_details()
    {
        using var harness = new CatalogHarness();
        var commands = harness.Services.GetRequiredService<ICatalogCommands>();
        var category = await commands.CreateCategoryAsync(new() { Name = "Electronics", Code = "electronics" }, default);
        var brand = await commands.CreateBrandAsync(new() { Name = "Generic", Code = "generic" }, default);
        var product = await commands.CreateProductAsync(new()
        {
            Name = "Mobile", CategoryId = category.Id, BrandId = brand.Id,
            Specifications = [new() { Name = "Connectivity", Value = "5G" }], Metadata = new() { ["Warranty"] = "12 months" }
        }, default);
        var variant = await commands.AddVariantAsync(product.Id, new() { Sku = "mobile-base" }, default);
        var updated = await commands.UpdateProductAsync(product.Id, new()
        {
            Name = "Mobile updated", Description = "Updated description", CategoryId = category.Id, BrandId = brand.Id,
            Status = "Active", WeightGrams = 200, Dimensions = new() { LengthMillimeters = 150, WidthMillimeters = 70, HeightMillimeters = 8 }
        }, default);
        Assert.Equal("Mobile updated", updated.Name);
        Assert.Equal("Active", updated.Status);
        Assert.Equal(variant.Id, Assert.Single(updated.Variants).Id);
        Assert.Equal(200, updated.WeightGrams);
        var queries = harness.Services.GetRequiredService<ICatalogQueries>();
        Assert.Equal(updated.Id, (await queries.GetProductAsync(product.Id, false, default)).Id);
    }
    [Fact]
    public async Task Dynamic_attribute_ownership_global_sku_and_combinations_are_validated()
    {
        using var harness = new CatalogHarness();
        var commands = harness.Services.GetRequiredService<ICatalogCommands>();
        var category = await commands.CreateCategoryAsync(new() { Name = "Cosmetics", Code = "cosmetics" }, default);
        var volume = await commands.CreateAttributeAsync(new() { Name = "Volume", Code = "volume" }, default);
        var value = await commands.AddAttributeValueAsync(volume.Id, new() { Label = "100 ml", Code = "100ml" }, default);
        var shade = await commands.CreateAttributeAsync(new() { Name = "Shade", Code = "shade" }, default);
        var wrongValue = await commands.AddAttributeValueAsync(shade.Id, new() { Label = "03", Code = "03" }, default);
        var product = await commands.CreateProductAsync(new()
        {
            Name = "Cosmetic", CategoryId = category.Id, Attributes = [new() { AttributeId = volume.Id, ValueIds = [value.Id] }]
        }, default);
        var variant = await commands.AddVariantAsync(product.Id, new() { Sku = "cosmetic-100", Attributes = [new() { AttributeId = volume.Id, ValueId = value.Id }] }, default);
        Assert.Equal("COSMETIC-100", variant.Sku);
        await Assert.ThrowsAsync<CatalogException>(() => commands.AddVariantAsync(product.Id, new() { Sku = "new-sku", Attributes = [new() { AttributeId = volume.Id, ValueId = value.Id }] }, default));
        await Assert.ThrowsAsync<CatalogException>(() => commands.AddVariantAsync(product.Id, new() { Sku = "wrong", Attributes = [new() { AttributeId = volume.Id, ValueId = wrongValue.Id }] }, default));
        var other = await commands.CreateProductAsync(new() { Name = "Other", CategoryId = category.Id }, default);
        var conflict = await Assert.ThrowsAsync<CatalogException>(() => commands.AddVariantAsync(other.Id, new() { Sku = "COSMETIC-100" }, default));
        Assert.Equal(409, conflict.StatusCode);
    }
    [Fact]
    public async Task Invalid_product_category_brand_and_attribute_references_are_rejected()
    {
        using var harness = new CatalogHarness();
        var commands = harness.Services.GetRequiredService<ICatalogCommands>();
        await Assert.ThrowsAsync<CatalogException>(() => commands.CreateProductAsync(new() { Name = "Invalid", CategoryId = Guid.NewGuid() }, default));
        var category = await commands.CreateCategoryAsync(new() { Name = "Category", Code = "category" }, default);
        await Assert.ThrowsAsync<CatalogException>(() => commands.CreateProductAsync(new() { Name = "Invalid", CategoryId = category.Id, BrandId = Guid.NewGuid() }, default));
        await Assert.ThrowsAsync<CatalogException>(() => commands.CreateProductAsync(new()
        { Name = "Invalid", CategoryId = category.Id, Attributes = [new() { AttributeId = Guid.NewGuid(), ValueIds = [Guid.NewGuid()] }] }, default));
    }
    [Fact]
    public async Task Inactive_and_draft_products_are_hidden_from_public_search()
    {
        using var harness = new CatalogHarness();
        var commands = harness.Services.GetRequiredService<ICatalogCommands>();
        var queries = harness.Services.GetRequiredService<ICatalogQueries>();
        var category = await commands.CreateCategoryAsync(new() { Name = "Grocery", Code = "grocery" }, default);
        var product = await commands.CreateProductAsync(new() { Name = "Coffee", CategoryId = category.Id }, default);
        Assert.Empty((await queries.ListProductsAsync(new() { Search = "Coffee" }, false, default)).Items);
        await commands.AddVariantAsync(product.Id, new() { Sku = "coffee" }, default);
        await commands.UpdateProductAsync(product.Id, new() { Name = "Coffee", CategoryId = category.Id, Status = "Active" }, default);
        Assert.Single((await queries.ListProductsAsync(new() { Search = "COFFEE" }, false, default)).Items);
        await commands.UpdateCategoryAsync(category.Id, new() { Name = "Grocery", Code = "grocery", IsActive = false }, default);
        Assert.Empty((await queries.ListProductsAsync(new(), false, default)).Items);
        Assert.Single((await queries.ListProductsAsync(new(), true, default)).Items);
    }
    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 101)]
    [InlineData(2147483647, 100)]
    public void Pagination_rejects_invalid_bounds_and_overflow(int page, int size) =>
        Assert.Throws<CatalogException>(() => CatalogValidation.Validate(new CatalogListQuery { Page = page, PageSize = size }));
    [Fact]
    public async Task Updating_variant_selection_and_replacing_details_preserves_persisted_identity()
    {
        using var harness = new CatalogHarness();
        var commands = harness.Services.GetRequiredService<ICatalogCommands>();
        var queries = harness.Services.GetRequiredService<ICatalogQueries>();
        var category = await commands.CreateCategoryAsync(new() { Name = "Clothing", Code = "clothing" }, default);
        var color = await commands.CreateAttributeAsync(new() { Name = "Color", Code = "color" }, default);
        var black = await commands.AddAttributeValueAsync(color.Id, new() { Label = "Black", Code = "black" }, default);
        var white = await commands.AddAttributeValueAsync(color.Id, new() { Label = "White", Code = "white" }, default);
        var input = new ProductInput
        {
            Name = "Shirt", CategoryId = category.Id,
            Attributes = [new() { AttributeId = color.Id, ValueIds = [black.Id, white.Id] }],
            Images = [new() { Url = "https://example.test/first.jpg" }],
            Specifications = [new() { Name = "Material", Value = "Cotton" }],
            Metadata = new() { ["Origin"] = "EU" }
        };
        var product = await commands.CreateProductAsync(input, default);
        var variant = await commands.AddVariantAsync(product.Id, new()
        { Sku = "shirt-black", Attributes = [new() { AttributeId = color.Id, ValueId = black.Id }] }, default);
        await commands.UpdateVariantAsync(product.Id, variant.Id, new()
        { Sku = "shirt-white", Attributes = [new() { AttributeId = color.Id, ValueId = white.Id }] }, default);
        await commands.UpdateProductAsync(product.Id, new()
        {
            Name = input.Name, CategoryId = category.Id,
            Images = [new() { Url = "https://example.test/replaced.jpg" }],
            Specifications = [new() { Name = "Material", Value = "Linen" }],
            Metadata = new() { ["Origin"] = "UK" },
            Attributes = [new() { AttributeId = color.Id, ValueIds = [white.Id] }]
        }, default);
        var saved = await queries.GetProductAsync(product.Id, true, default);
        Assert.Equal(variant.Id, Assert.Single(saved.Variants).Id);
        Assert.Equal(white.Id, Assert.Single(saved.Variants[0].Attributes).ValueId);
        Assert.Single(saved.AttributeOptions);
        Assert.Equal("https://example.test/replaced.jpg", Assert.Single(saved.Images).Url);
        Assert.Equal("Linen", Assert.Single(saved.Specifications).Value);
        Assert.Equal("UK", saved.Metadata["Origin"]);
    }
    [Fact]
    public void Nested_validation_rejects_empty_ids_duplicate_values_and_invalid_dimensions()
    {
        Assert.Throws<CatalogException>(() => CatalogValidation.Validate(new ProductInput { Name = "Product" }));
        var value = Guid.NewGuid();
        Assert.Throws<CatalogException>(() => CatalogValidation.Validate(new ProductInput
        { Name = "Product", CategoryId = Guid.NewGuid(), Attributes = [new() { AttributeId = Guid.NewGuid(), ValueIds = [value, value] }] }));
        Assert.Throws<CatalogException>(() => CatalogValidation.Validate(new VariantInput
        { Sku = "sku", Dimensions = new() { LengthMillimeters = 1, WidthMillimeters = 1, HeightMillimeters = 0 } }));
    }
}
