using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Order.Contracts;

namespace MyOnlineShop.Order.Infrastructure.Persistence;

public sealed class OrderRefundSnapshots(OrderDbContext db) : IOrderRefundSnapshots
{
    public Task<OrderRefundSnapshot?> GetAsync(Guid id, CancellationToken ct) => db.Orders.AsNoTracking().Where(value => value.Id == id)
        .Select(value => new OrderRefundSnapshot(value.Id, value.UserId, value.PayableAmount, value.Currency)).SingleOrDefaultAsync(ct);
}
