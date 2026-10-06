using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Refund.Application;
using MyOnlineShop.Refund.Domain;
using RefundAggregate = MyOnlineShop.Refund.Domain.Refund;

namespace MyOnlineShop.Refund.Infrastructure.Persistence;

public sealed class RefundStore(RefundDbContext db) : IRefundStore
{
    public Task<RefundAggregate?> GetByPaymentAsync(Guid id, CancellationToken ct) => db.Refunds.SingleOrDefaultAsync(value => value.PaymentId == id, ct);
    public async Task<RefundAggregate?> GetAsync(Guid id, CancellationToken ct)
    {
        var tracked = db.Refunds.Local.SingleOrDefault(value => value.Id == id);
        if (tracked is not null) await db.Entry(tracked).ReloadAsync(ct);
        return await db.Refunds.SingleOrDefaultAsync(value => value.Id == id, ct);
    }
    public async Task<IReadOnlyList<Guid>> DueAsync(DateTimeOffset now, int count, CancellationToken ct) =>
        await db.Refunds.AsNoTracking().Where(value => value.Status == RefundStatus.Pending && value.DueAtUtc <= now && value.NextAttemptAtUtc <= now)
            .OrderBy(value => value.NextAttemptAtUtc).ThenBy(value => value.Id).Take(count).Select(value => value.Id).ToListAsync(ct);
    public void Add(RefundAggregate refund) => db.Refunds.Add(refund);
    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
public sealed class RefundUnitOfWork(RefundDbContext db, IEnumerable<ILocalSqlTransactionParticipant> participants) : IRefundUnitOfWork
{
    public async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        // Existing Inbox owns the transaction when invoked through a broker consumer.
        if (db.Database.CurrentTransaction is not null) { await action(ct); await db.SaveChangesAsync(ct); return; }
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var messaging = participants.Single(value => value.Name == "messaging");
            try
            {
                await messaging.EnlistAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), ct);
                await action(ct); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            }
            finally { await messaging.DetachAsync(CancellationToken.None); }
        });
    }
    public Task LockAsync(Guid payment, CancellationToken ct)
    {
        var resource = $"Refund:{payment:N}";
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 51011, 'Refund is busy.', 1;
            """, ct);
    }
}
