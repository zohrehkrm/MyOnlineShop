using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Cart.Application;
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Cart.Domain;
using MyOnlineShop.Cart.Infrastructure.Persistence;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using MyOnlineShop.Inventory.Domain;
using Xunit;
using CartAggregate = MyOnlineShop.Cart.Domain.Cart;

namespace MyOnlineShop.Cart.Tests;

public sealed class CartBehaviorTests
{
    [Fact]
    public async Task Empty_query_is_read_only_and_first_add_creates_one_active_cart()
    {
        using var harness = new CartHarness(); var user = Guid.NewGuid();
        var queries = harness.Services.GetRequiredService<ICartQueries>();
        var commands = harness.Services.GetRequiredService<ICartCommands>();
        Assert.Null((await queries.GetCurrentAsync(user, default)).Id);
        var context = harness.Services.GetRequiredService<CartDbContext>();
        Assert.Empty(await context.Carts.ToListAsync());
        var added = await commands.AddAsync(user, new() { ProductVariantId = harness.Catalog.Id, Quantity = 1 }, default);
        var current = await queries.GetCurrentAsync(user, default);
        Assert.Equal(added.Id, current.Id); Assert.Single(current.Items); Assert.Single(await context.Carts.ToListAsync());
    }
    [Fact]
    public async Task Duplicate_add_update_remove_and_clear_preserve_expected_item_identity()
    {
        using var harness = new CartHarness(); var user = Guid.NewGuid();
        var commands = harness.Services.GetRequiredService<ICartCommands>();
        var first = await commands.AddAsync(user, new() { ProductVariantId = harness.Catalog.Id, Quantity = 2 }, default);
        var item = Assert.Single(first.Items);
        var second = await commands.AddAsync(user, new() { ProductVariantId = harness.Catalog.Id, Quantity = 3 }, default);
        Assert.Equal(item.Id, Assert.Single(second.Items).Id); Assert.Equal(5, second.Items[0].Quantity);
        var updated = await commands.UpdateQuantityAsync(user, item.Id, new() { Quantity = 7 }, default);
        Assert.Equal(7, Assert.Single(updated.Items).Quantity);
        await commands.RemoveAsync(user, item.Id, default);
        Assert.Empty((await harness.Services.GetRequiredService<ICartQueries>().GetCurrentAsync(user, default)).Items);
        await commands.AddAsync(user, new() { ProductVariantId = harness.Catalog.Id, Quantity = 1 }, default);
        await commands.ClearAsync(user, default); await commands.ClearAsync(user, default);
        Assert.Empty((await harness.Services.GetRequiredService<ICartQueries>().GetCurrentAsync(user, default)).Items);
        Assert.Empty(await harness.Services.GetRequiredService<CartDbContext>().Items.AsNoTracking().ToListAsync());
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1000)] [InlineData(int.MaxValue)]
    public async Task Invalid_quantities_are_rejected_before_cart_creation(int quantity)
    {
        using var harness = new CartHarness(); var commands = harness.Services.GetRequiredService<ICartCommands>();
        await Assert.ThrowsAsync<CartException>(() => commands.AddAsync(Guid.NewGuid(), new() { ProductVariantId = harness.Catalog.Id, Quantity = quantity }, default));
        Assert.Empty(await harness.Services.GetRequiredService<CartDbContext>().Carts.ToListAsync());
    }
    [Fact]
    public async Task Invalid_or_inactive_variants_are_rejected_and_existing_items_can_be_removed()
    {
        using var harness = new CartHarness(); var user = Guid.NewGuid();
        var commands = harness.Services.GetRequiredService<ICartCommands>();
        await Assert.ThrowsAsync<CartException>(() => commands.AddAsync(user, new() { ProductVariantId = Guid.NewGuid(), Quantity = 1 }, default));
        await Assert.ThrowsAsync<CartException>(() => commands.AddAsync(user, new() { ProductVariantId = Guid.Empty, Quantity = 1 }, default));
        var cart = await commands.AddAsync(user, new() { ProductVariantId = harness.Catalog.Id, Quantity = 1 }, default);
        harness.Catalog.Active = false;
        await Assert.ThrowsAsync<CartException>(() => commands.AddAsync(user, new() { ProductVariantId = harness.Catalog.Id, Quantity = 1 }, default));
        await Assert.ThrowsAsync<CartException>(() => commands.UpdateQuantityAsync(user, cart.Items[0].Id, new() { Quantity = 2 }, default));
        await commands.RemoveAsync(user, cart.Items[0].Id, default);
    }
    [Fact]
    public async Task User_isolation_prevents_read_update_remove_or_clear_of_another_cart()
    {
        using var harness = new CartHarness(); var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var commands = harness.Services.GetRequiredService<ICartCommands>(); var queries = harness.Services.GetRequiredService<ICartQueries>();
        var cart = await commands.AddAsync(owner, new() { ProductVariantId = harness.Catalog.Id, Quantity = 2 }, default);
        Assert.Empty((await queries.GetCurrentAsync(other, default)).Items);
        await Assert.ThrowsAsync<CartException>(() => commands.UpdateQuantityAsync(other, cart.Items[0].Id, new() { Quantity = 3 }, default));
        await Assert.ThrowsAsync<CartException>(() => commands.RemoveAsync(other, cart.Items[0].Id, default));
        await commands.ClearAsync(other, default);
        Assert.Equal(2, Assert.Single((await queries.GetCurrentAsync(owner, default)).Items).Quantity);
    }
    [Fact]
    public async Task Adding_cart_quantity_does_not_change_inventory_or_create_movements()
    {
        using var harness = new CartHarness();
        using var inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var warehouse = Warehouse.Create("Stock fixture", "fixture", true);
        var stock = Stock.Create(warehouse.Id, harness.Catalog.Id);
        inventory.Warehouses.Add(warehouse); inventory.Stocks.Add(stock);
        inventory.Entry(stock).Property(value => value.Quantity).CurrentValue = 1;
        await inventory.SaveChangesAsync();
        var user = Guid.NewGuid(); var commands = harness.Services.GetRequiredService<ICartCommands>();
        var cart = await commands.AddAsync(user, new() { ProductVariantId = harness.Catalog.Id, Quantity = 1 }, default);
        Assert.Equal(1, Assert.Single(cart.Items).Quantity);
        // Requested quantity can exceed available stock; Cart never consults Inventory.
        await commands.UpdateQuantityAsync(user, cart.Items[0].Id, new() { Quantity = 5 }, default);
        Assert.Equal(1, (await inventory.Stocks.AsNoTracking().SingleAsync()).Quantity);
        Assert.Empty(await inventory.Movements.ToListAsync());
        foreach (var assembly in new[] { typeof(CartAggregate).Assembly, typeof(CartCommands).Assembly, typeof(CartDbContext).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name!.Contains("Inventory") || reference.Name.Contains("Pricing"));
    }
    [Fact]
    public void Domain_limits_and_revision_protect_root_mutations()
    {
        var cart = CartAggregate.Create(Guid.NewGuid(), DateTimeOffset.UtcNow); var variant = Guid.NewGuid();
        var revision = cart.Revision; cart.Add(variant, 999, DateTimeOffset.UtcNow);
        Assert.NotEqual(revision, cart.Revision);
        Assert.Throws<CartRuleException>(() => cart.Add(variant, 1, DateTimeOffset.UtcNow));
        Assert.Equal(999, Assert.Single(cart.Items).Quantity);
        for (var index = 1; index < CartAggregate.MaximumItems; index++) cart.Add(Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
        Assert.Throws<CartRuleException>(() => cart.Add(Guid.NewGuid(), 1, DateTimeOffset.UtcNow));
        Assert.Throws<CartRuleException>(() => CartAggregate.Create(Guid.Empty, DateTimeOffset.UtcNow));
    }
}
