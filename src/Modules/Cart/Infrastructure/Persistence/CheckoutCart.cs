using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Cart.Application;
using MyOnlineShop.Cart.Contracts;

namespace MyOnlineShop.Cart.Infrastructure.Persistence;

public sealed class CheckoutCart(CartDbContext context, ICartRepository repository, TimeProvider clock)
    : ICheckoutCart, ILocalSqlTransactionParticipant
{
    private string? _originalConnectionString;
    public string Name => "cart";
    public async Task EnlistAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is not null) throw new InvalidOperationException("Cart already has a transaction.");
        context.ChangeTracker.Clear();
        _originalConnectionString = context.Database.GetConnectionString();
        context.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await context.Database.UseTransactionAsync(transaction, ct);
    }
    public async Task DetachAsync(CancellationToken ct)
    {
        await context.Database.UseTransactionAsync(null, ct);
        // Replacing an EF-owned connection disposes the old object. Restore its
        // configuration rather than reusing that disposed connection instance.
        context.Database.SetDbConnection(null);
        context.Database.SetConnectionString(_originalConnectionString);
        _originalConnectionString = null; context.ChangeTracker.Clear();
    }
    public async Task<CheckoutCartSnapshot> GetAsync(Guid userId, CancellationToken ct)
    {
        CartValidation.User(userId);
        return await context.Carts.AsNoTracking().Where(cart => cart.UserId == userId && cart.IsActive)
            .Select(cart => new CheckoutCartSnapshot(new CartDto(cart.Id, cart.IsActive, cart.CreatedAtUtc, cart.UpdatedAtUtc,
                cart.Items.OrderBy(item => item.Id).Select(item => new CartItemDto(item.Id, item.ProductVariantId, item.Quantity, item.CreatedAtUtc, item.UpdatedAtUtc)).ToList()), cart.Revision))
            .SingleOrDefaultAsync(ct) ?? new(CartMapping.Empty(), Guid.Empty);
    }
    public async Task ClearAsync(Guid userId, Guid cartId, Guid revision, CancellationToken ct)
    {
        CartValidation.User(userId);
        if (context.Database.IsRelational() && context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Checkout Cart clearing requires the coordinated transaction.");
        var cart = await repository.GetActiveAsync(userId, ct);
        if (cart is null || cart.Id != cartId || cart.Revision != revision) throw CartException.Conflict();
        cart.Clear(clock.GetUtcNow());
        try { await context.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw CartException.Conflict(); }
    }
}
