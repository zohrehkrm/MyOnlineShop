using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Refund.Application;
using MyOnlineShop.Refund.Contracts;
using MyOnlineShop.Refund.Domain;
using MyOnlineShop.Refund.Infrastructure.Persistence;
using MyOnlineShop.Wallet.Contracts;
using RefundAggregate = MyOnlineShop.Refund.Domain.Refund;

namespace MyOnlineShop.Refund.Tests;

internal sealed class Clock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class Orders : IOrderRefundSnapshots
{
    public OrderRefundSnapshot? Snapshot { get; set; }
    public Task<OrderRefundSnapshot?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Snapshot?.OrderId == id ? Snapshot : null);
}
// Evidence exists only in tests. Production has no Phase 8 adapter and never substitutes this.
internal sealed class Evidence : IRefundPaymentEvidence
{
    public VerifiedRefundPayment? Proof { get; set; }
    public Task<VerifiedRefundPayment?> GetSucceededPaymentAsync(Guid id, CancellationToken ct) => Task.FromResult(Proof);
}
internal sealed class Outbox : IOutboxWriter
{
    public List<IIntegrationEvent> Events { get; } = [];
    public Task EnqueueAsync(IIntegrationEvent message, string correlationId, CancellationToken ct)
    { Events.Add(message); return Task.CompletedTask; }
}
internal sealed class Store(RefundDbContext db) : IRefundStore
{
    private readonly RefundStore _inner = new(db);
    public bool FailCompletionOnce { get; set; }
    public Task<RefundAggregate?> GetByPaymentAsync(Guid id, CancellationToken ct) => _inner.GetByPaymentAsync(id, ct);
    public Task<RefundAggregate?> GetAsync(Guid id, CancellationToken ct) => _inner.GetAsync(id, ct);
    public Task<IReadOnlyList<Guid>> DueAsync(DateTimeOffset now, int count, CancellationToken ct) => _inner.DueAsync(now, count, ct);
    public void Add(RefundAggregate refund) => _inner.Add(refund);
    public Task SaveAsync(CancellationToken ct)
    {
        if (FailCompletionOnce && db.ChangeTracker.Entries<RefundAggregate>().Any(value => value.Entity.Status == RefundStatus.Completed))
        { FailCompletionOnce = false; throw new IOException("Injected refund completion failure"); }
        return _inner.SaveAsync(ct);
    }
}
// Explicit InMemory test unit; no claim of SQL transaction/lock/concurrency verification.
internal sealed class Unit(RefundDbContext db) : IRefundUnitOfWork
{
    public Task LockAsync(Guid payment, CancellationToken ct) => Task.CompletedTask;
    public async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    { db.ChangeTracker.Clear(); try { await action(ct); await db.SaveChangesAsync(ct); } catch { db.ChangeTracker.Clear(); throw; } }
}
internal sealed class WalletFaults(IWalletOperations inner) : IWalletOperations
{
    public int Failures { get; set; }
    public List<(Guid User, Guid Actor, WalletOperation Operation)> Calls { get; } = [];
    public Task<WalletTransactionDto> CreditAsync(Guid user, Guid actor, WalletOperation input, CancellationToken ct)
    {
        Calls.Add((user, actor, input));
        if (Failures-- > 0) throw new IOException("credential=never-log-this");
        return inner.CreditAsync(user, actor, input, ct);
    }
    public Task<WalletTransactionDto> DebitAsync(Guid user, Guid actor, WalletOperation input, CancellationToken ct) => throw new NotSupportedException();
}
internal sealed class Harness : IDisposable
{
    public MyOnlineShop.Wallet.Tests.Harness Wallet { get; } = new();
    public Clock Clock { get; } = new();
    public RefundDbContext Db { get; } = new(new DbContextOptionsBuilder<RefundDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public Orders Orders { get; } = new();
    public Evidence Evidence { get; } = new();
    public Outbox Outbox { get; } = new();
    public RefundProcessingOptions Options { get; } = new();
    public Store Store { get; }
    public Unit Unit { get; }
    public WalletFaults Credits { get; }
    public Guid Payment { get; } = Guid.NewGuid();
    public Guid Order { get; } = Guid.NewGuid();
    public Guid RefundId => RefundAggregate.StableId("inventory-refund", Payment);
    public RefundRequests Requests => new(Store, Unit, Orders, Outbox, Microsoft.Extensions.Options.Options.Create(Options), Clock);
    public RefundScheduler Scheduler(bool hasEvidence = true) => new(Store, Unit, Outbox, hasEvidence ? [Evidence] : [], Microsoft.Extensions.Options.Options.Create(Options), Clock);
    public RefundCompletion Completion(bool hasEvidence = true) => new(Store, Unit, Credits, hasEvidence ? [Evidence] : [], Outbox, Microsoft.Extensions.Options.Options.Create(Options), Clock);
    public Harness()
    {
        Store = new(Db); Unit = new(Db); Credits = new(Wallet.Operations);
        Orders.Snapshot = new(Order, Wallet.Identity.User, 500_000m, "IRR");
        Evidence.Proof = new(Payment, Order, Wallet.Identity.User, 500_000m, "IRR");
    }
    public Task Request() => Requests.RequestAsync(Payment, Order, "refund-test", default);
    public async Task<RefundAggregate> Read() { Db.ChangeTracker.Clear(); return await Db.Refunds.SingleAsync(); }
    public async Task Due() { Clock.Now = Clock.Now.AddHours(1); await Scheduler().ScheduleAsync(default); }
    public void Dispose() { Db.Dispose(); Wallet.Dispose(); }
}
