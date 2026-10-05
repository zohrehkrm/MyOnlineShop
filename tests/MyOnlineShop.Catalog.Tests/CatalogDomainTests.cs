using MyOnlineShop.Catalog.Domain;
using Xunit;

namespace MyOnlineShop.Catalog.Tests;

public sealed class CatalogDomainTests
{
    private static Product ProductWithOptions(out Guid attribute, out Guid firstValue, out Guid secondValue)
    {
        attribute = Guid.NewGuid(); firstValue = Guid.NewGuid(); secondValue = Guid.NewGuid();
        var product = Product.Create("Generic product", null, Guid.NewGuid(), null, ProductKind.Physical, DateTimeOffset.UtcNow);
        product.ConfigureAttributes([new(attribute, firstValue), new(attribute, secondValue)]);
        return product;
    }
    [Fact]
    public void Hierarchy_rejects_self_parent_and_descendant_cycles()
    {
        var category = Category.Create("Category", "category", null, true);
        Assert.Throws<CatalogRuleException>(() => category.Update("Category", "category", category.Id, true));
        Assert.Throws<CatalogRuleException>(() => Category.ValidateParentChain(category.Id, [Guid.NewGuid(), category.Id]));
        var ancestor = Guid.NewGuid();
        Assert.Throws<CatalogRuleException>(() => Category.ValidateParentChain(category.Id, [ancestor, ancestor]));
    }
    [Fact]
    public void Attributes_are_generic_reusable_and_value_codes_are_unique()
    {
        foreach (var name in new[] { "RAM", "Storage", "Volume", "Shade", "Color", "Size" })
        {
            var attribute = CatalogAttribute.Create(name, name, true);
            var value = attribute.AddValue("Choice", "choice", true);
            Assert.Equal(attribute.Id, value.AttributeId);
            Assert.Throws<CatalogRuleException>(() => attribute.AddValue("Duplicate", "CHOICE", true));
        }
    }
    [Fact]
    public void Variant_combinations_are_order_independent_and_unique()
    {
        var product = ProductWithOptions(out var attribute, out var first, out var second);
        var anotherAttribute = Guid.NewGuid(); var anotherValue = Guid.NewGuid();
        product.ConfigureAttributes([new(attribute, first), new(attribute, second), new(anotherAttribute, anotherValue)]);
        var selected = new[] { new AttributeOption(attribute, first), new AttributeOption(anotherAttribute, anotherValue) };
        product.AddVariant("sku-1", true, null, null, selected, DateTimeOffset.UtcNow);
        Assert.Throws<CatalogRuleException>(() => product.AddVariant("sku-2", true, null, null, selected.Reverse().ToArray(), DateTimeOffset.UtcNow));
        Assert.Equal(ProductVariant.HashCombination(selected), ProductVariant.HashCombination(selected.Reverse()));
    }
    [Fact]
    public void Variants_require_exactly_one_allowed_value_per_attribute()
    {
        var product = ProductWithOptions(out var attribute, out var first, out var second);
        foreach (var values in new AttributeOption[][]
        {
            [], [new(attribute, first), new(attribute, second)], [new(attribute, Guid.NewGuid())], [new(Guid.NewGuid(), first)]
        })
            Assert.Throws<CatalogRuleException>(() => product.AddVariant("sku", true, null, null, values, DateTimeOffset.UtcNow));
    }
    [Fact]
    public void Product_schema_and_used_options_cannot_be_removed_after_variant_creation()
    {
        var product = ProductWithOptions(out var attribute, out var first, out var second);
        product.AddVariant("sku", true, null, null, [new(attribute, first)], DateTimeOffset.UtcNow);
        Assert.Throws<CatalogRuleException>(() => product.ConfigureAttributes([]));
        Assert.Throws<CatalogRuleException>(() => product.ConfigureAttributes([new(attribute, second)]));
        product.ConfigureAttributes([new(attribute, first)]);
    }
    [Fact]
    public void Plain_products_have_one_base_variant_and_digital_products_reject_measurements()
    {
        var product = Product.Create("Digital download", null, Guid.NewGuid(), null, ProductKind.Digital, DateTimeOffset.UtcNow);
        product.AddVariant("download", true, null, null, [], DateTimeOffset.UtcNow);
        Assert.Throws<CatalogRuleException>(() => product.AddVariant("other", true, null, null, [], DateTimeOffset.UtcNow));
        Assert.Throws<CatalogRuleException>(() => product.UpdateVariant(product.Variants[0].Id, "download", true, 1, null, [], DateTimeOffset.UtcNow));
    }
    [Fact]
    public void Active_product_requires_active_variant_and_cannot_lose_its_last_one()
    {
        var product = Product.Create("Product", null, Guid.NewGuid(), null, ProductKind.Physical, DateTimeOffset.UtcNow);
        Assert.Throws<CatalogRuleException>(() => product.Update(product.Name, null, product.CategoryId, null, product.Kind, ProductStatus.Active, null, null, DateTimeOffset.UtcNow));
        var variant = product.AddVariant("sku", true, null, null, [], DateTimeOffset.UtcNow);
        product.Update(product.Name, null, product.CategoryId, null, product.Kind, ProductStatus.Active, null, null, DateTimeOffset.UtcNow);
        Assert.Throws<CatalogRuleException>(() => product.UpdateVariant(variant.Id, "sku", false, null, null, [], DateTimeOffset.UtcNow));
    }
    [Fact]
    public void Images_have_stable_order_and_one_primary_separate_from_specifications()
    {
        var product = Product.Create("Product", null, Guid.NewGuid(), null, ProductKind.Physical, DateTimeOffset.UtcNow);
        product.ReplaceDetails([new("https://example.test/two.png", null, 2, false), new("https://example.test/one.png", "Alt", 1, false)],
            [new("Material", "Cotton", null, 0)], new Dictionary<string, string> { ["Origin"] = "Local" });
        Assert.Single(product.Images, image => image.IsPrimary && image.SortOrder == 1);
        Assert.Empty(product.AttributeOptions);
        Assert.Single(product.Specifications);
        Assert.Throws<CatalogRuleException>(() => product.ReplaceDetails([new("http://example.test/image.png", null, 0, true)], [], new Dictionary<string, string>()));
        Assert.Throws<CatalogRuleException>(() => product.ReplaceDetails([new("https://example.test/1.png", null, 0, true), new("https://example.test/2.png", null, 0, true)], [], new Dictionary<string, string>()));
    }
    [Theory]
    [InlineData(" ")]
    [InlineData("unsafe sku")]
    [InlineData("sku/invalid")]
    public void Invalid_sku_rules_are_enforced_in_domain(string sku)
    {
        var product = Product.Create("Product", null, Guid.NewGuid(), null, ProductKind.Physical, DateTimeOffset.UtcNow);
        Assert.Throws<CatalogRuleException>(() => product.AddVariant(sku, true, null, null, [], DateTimeOffset.UtcNow));
    }
}
