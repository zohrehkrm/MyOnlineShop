using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Order.Contracts;

namespace MyOnlineShop.Order.Infrastructure.Persistence;

public sealed class OrderShippingSnapshots(OrderDbContext db) : IOrderShippingSnapshots
{
    public Task<OrderShippingSnapshot?> GetAsync(Guid id, CancellationToken ct) => db.Orders.AsNoTracking().Where(value => value.Id == id)
        .Select(value => new OrderShippingSnapshot(value.Id, value.UserId, value.Status.ToString(), value.Items.Any(item => item.ProductKind == "Physical"), value.Shipping))
        .SingleOrDefaultAsync(ct);
}
