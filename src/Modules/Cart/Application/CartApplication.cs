using System.ComponentModel.DataAnnotations;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Cart.Domain;
using MyOnlineShop.Catalog.Contracts;
using CartAggregate = MyOnlineShop.Cart.Domain.Cart;

namespace MyOnlineShop.Cart.Application;

public sealed class CartException(string code, int status, string message) : Exception(message), IApplicationError
{
    public string Code => code;
    public int StatusCode => status;
    public string SafeMessage => Message;
    public static CartException Invalid(string message = "Cart input is invalid.") => new("cart_validation", 400, message);
    public static CartException NotFound() => new("cart_not_found", 404, "Cart item not found.");
    public static CartException Conflict() => new("cart_concurrency", 409, "Cart changed. Reload and retry.");
}
public static class CartValidation
{
    public static void User(Guid userId)
    { if (userId == Guid.Empty) throw new CartException("cart_user", 401, "A valid authenticated user is required."); }
    public static void Validate(object input)
    {
        if (!Validator.TryValidateObject(input, new ValidationContext(input), [], true) ||
            input is AddCartItem { ProductVariantId: var id } && id == Guid.Empty) throw CartException.Invalid();
    }
}
public interface ICartRepository
{
    Task<CartAggregate?> GetActiveAsync(Guid userId, CancellationToken ct);
    void Add(CartAggregate cart);
}
public interface ICartUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
}
public interface ICartReadStore : ICartQueries;
public static class CartMapping
{
    public static CartDto Empty() => new(null, true, null, null, []);
    public static CartDto Dto(CartAggregate cart) => new(cart.Id, cart.IsActive, cart.CreatedAtUtc, cart.UpdatedAtUtc,
        cart.Items.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id).Select(item =>
            new CartItemDto(item.Id, item.ProductVariantId, item.Quantity, item.CreatedAtUtc, item.UpdatedAtUtc)).ToArray());
}
public sealed class AddCartItemHandler(ICartRepository carts, ICartUnitOfWork unit, ICatalogVariantReferences catalog, TimeProvider clock)
{
    public async Task<CartDto> HandleAsync(Guid userId, AddCartItem command, CancellationToken ct)
    {
        CartValidation.User(userId); CartValidation.Validate(command);
        return await unit.ExecuteAsync(async token =>
        {
            var variant = await catalog.GetAsync(command.ProductVariantId, token);
            if (variant is null || !variant.IsActive) throw CartException.Invalid("Product variant is missing or not purchasable.");
            var cart = await carts.GetActiveAsync(userId, token);
            if (cart is null) { cart = CartAggregate.Create(userId, clock.GetUtcNow()); carts.Add(cart); }
            cart.Add(command.ProductVariantId, command.Quantity, clock.GetUtcNow());
            return CartMapping.Dto(cart);
        }, ct);
    }
}
public sealed class UpdateCartItemQuantityHandler(ICartRepository carts, ICartUnitOfWork unit, ICatalogVariantReferences catalog, TimeProvider clock)
{
    public async Task<CartDto> HandleAsync(Guid userId, Guid itemId, UpdateCartItemQuantity command, CancellationToken ct)
    {
        CartValidation.User(userId); CartValidation.Validate(command);
        return await unit.ExecuteAsync(async token =>
        {
            var cart = await carts.GetActiveAsync(userId, token) ?? throw CartException.NotFound();
            var item = cart.Items.SingleOrDefault(value => value.Id == itemId) ?? throw CartException.NotFound();
            var variant = await catalog.GetAsync(item.ProductVariantId, token);
            if (variant is null || !variant.IsActive) throw CartException.Invalid("Product variant is missing or not purchasable.");
            cart.UpdateQuantity(itemId, command.Quantity, clock.GetUtcNow()); return CartMapping.Dto(cart);
        }, ct);
    }
}
public sealed class RemoveCartItemHandler(ICartRepository carts, ICartUnitOfWork unit, TimeProvider clock)
{
    public async Task HandleAsync(Guid userId, Guid itemId, CancellationToken ct)
    {
        CartValidation.User(userId);
        await unit.ExecuteAsync(async token =>
        {
            var cart = await carts.GetActiveAsync(userId, token) ?? throw CartException.NotFound();
            if (!cart.Items.Any(item => item.Id == itemId)) throw CartException.NotFound();
            cart.Remove(itemId, clock.GetUtcNow()); return true;
        }, ct);
    }
}
public sealed class ClearCartHandler(ICartRepository carts, ICartUnitOfWork unit, TimeProvider clock)
{
    public async Task HandleAsync(Guid userId, CancellationToken ct)
    {
        CartValidation.User(userId);
        await unit.ExecuteAsync(async token =>
        {
            var cart = await carts.GetActiveAsync(userId, token);
            if (cart is not null) cart.Clear(clock.GetUtcNow());
            return true;
        }, ct);
    }
}
public sealed class CartCommands(AddCartItemHandler add, UpdateCartItemQuantityHandler update, RemoveCartItemHandler remove, ClearCartHandler clear) : ICartCommands
{
    public Task<CartDto> AddAsync(Guid userId, AddCartItem command, CancellationToken ct) => add.HandleAsync(userId, command, ct);
    public Task<CartDto> UpdateQuantityAsync(Guid userId, Guid itemId, UpdateCartItemQuantity command, CancellationToken ct) => update.HandleAsync(userId, itemId, command, ct);
    public Task RemoveAsync(Guid userId, Guid itemId, CancellationToken ct) => remove.HandleAsync(userId, itemId, ct);
    public Task ClearAsync(Guid userId, CancellationToken ct) => clear.HandleAsync(userId, ct);
}
public sealed class GetCurrentCartHandler(ICartReadStore store) : ICartQueries
{
    public Task<CartDto> GetCurrentAsync(Guid userId, CancellationToken ct)
    { CartValidation.User(userId); return store.GetCurrentAsync(userId, ct); }
}
