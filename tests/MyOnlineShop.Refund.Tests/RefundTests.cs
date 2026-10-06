using Microsoft.EntityFrameworkCore;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Refund.Application;
using MyOnlineShop.Refund.Contracts;
using MyOnlineShop.Refund.Domain;
using Xunit;
using RefundAggregate = MyOnlineShop.Refund.Domain.Refund;

namespace MyOnlineShop.Refund.Tests;

public sealed class RefundTests
{
    [Fact]
    public async Task Existing_inventory_shortage_event_drives_pending_then_delayed_wallet_refund()
    {
        using var h = new Harness(); var stock = new MyOnlineShop.Inventory.Tests.CommandHarness();
        await stock.Commands.ReceiveAsync(stock.Input(1), stock.Actor, default);
        var payment = new PaymentSucceededIntegrationEventV1(Guid.NewGuid(), h.Clock.Now, h.Payment, h.Order, h.Wallet.Identity.User,
            500_000m, "IRR", [new(stock.Warehouse, stock.Catalog.Id, 2)]);
        var consumer = new MyOnlineShop.Inventory.Application.PaymentSucceededConsumer(stock.Commands, h.Clock);
        var failure = Assert.IsType<InventoryUnavailableIntegrationEventV1>(await consumer.HandleAsync(MessageEnvelope.From(payment, "shortage"), default));
        Assert.Equal(1, stock.Store.Stocks[(stock.Warehouse, stock.Catalog.Id)].Quantity);
        await new InventoryUnavailableConsumer(h.Requests).HandleAsync(MessageEnvelope.From(failure, "shortage"), default);
        Assert.Empty(h.Credits.Calls); Assert.Equal(RefundStatus.Pending, (await h.Read()).Status);
        await h.Due(); await h.Completion().ProcessAsync(h.RefundId, 1, default);
        Assert.Equal(RefundStatus.Completed, (await h.Read()).Status); Assert.Single(await h.Wallet.Db.Ledger.ToListAsync());
        // Explicit stock/persistence/evidence test doubles; not a true Payment producer or live SQL integration.
    }
    [Fact]
    public async Task Request_uses_server_snapshot_and_persists_default_hour_schedule_and_outbox()
    {
        using var h = new Harness(); await h.Request(); var refund = await h.Read();
        Assert.Equal(h.Wallet.Identity.User, refund.UserId); Assert.Equal(500_000m, refund.Amount); Assert.Equal("IRR", refund.Currency);
        Assert.Equal(RefundStatus.Pending, refund.Status); Assert.Equal(h.Clock.Now.AddHours(1), refund.DueAtUtc);
        Assert.Equal(h.RefundId, refund.Id); Assert.NotEqual(Guid.Empty, refund.IdempotencyKey);
        Assert.IsType<RefundRequestedIntegrationEventV1>(Assert.Single(h.Outbox.Events)); Assert.Empty(h.Credits.Calls);
    }
    [Fact]
    public async Task Duplicate_request_and_changed_order_are_safe()
    {
        using var h = new Harness(); await h.Request(); h.Clock.Now = h.Clock.Now.AddMinutes(5);
        h.Orders.Snapshot = h.Orders.Snapshot! with { PayableAmount = 900_000m }; await h.Request();
        Assert.Single(h.Outbox.Events); Assert.Equal(500_000m, (await h.Read()).Amount);
        await Assert.ThrowsAsync<InvalidMessageException>(() => h.Requests.RequestAsync(h.Payment, Guid.NewGuid(), "x", default));
        Assert.Single(await h.Db.Refunds.ToListAsync());
    }
    [Fact]
    public async Task Inventory_failure_consumer_requests_refund_but_never_credits_synchronously()
    {
        using var h = new Harness(); var consumer = new InventoryUnavailableConsumer(h.Requests);
        var failure = new InventoryUnavailableIntegrationEventV1(Guid.NewGuid(), h.Clock.Now, h.Payment, h.Order, "StockUnavailable");
        var message = MessageEnvelope.From(failure, "inventory-correlation");
        await consumer.HandleAsync(message, default); await consumer.HandleAsync(message, default);
        await consumer.HandleAsync(MessageEnvelope.From(failure with { EventId = Guid.NewGuid() }, "another-delivery"), default);
        Assert.Equal("inventory-correlation", (await h.Read()).CorrelationId); Assert.Single(h.Outbox.Events); Assert.Empty(h.Credits.Calls);
        await Assert.ThrowsAsync<InvalidMessageException>(() => consumer.HandleAsync(MessageEnvelope.From(failure with { Reason = "Arbitrary" }, "x"), default));
        Assert.Equal(new[] { "refund" }, consumer.TransactionParticipants);
    }
    [Fact]
    public async Task Durable_eligibility_uses_configured_delay_and_survives_a_new_scheduler()
    {
        using var h = new Harness(); h.Options.DelayMinutes = 120; await h.Request(); await h.Due();
        Assert.Equal(RefundStatus.Pending, (await h.Read()).Status); Assert.Single(h.Outbox.Events);
        h.Clock.Now = h.Clock.Now.AddHours(1); Assert.Equal(1, await h.Scheduler().ScheduleAsync(default));
        Assert.Equal(0, await h.Scheduler().ScheduleAsync(default));
        Assert.Equal(RefundStatus.Processing, (await h.Read()).Status); Assert.Single(h.Outbox.Events.OfType<RefundDueIntegrationEventV1>());
    }
    [Fact]
    public async Task Missing_payment_dependency_keeps_requests_pending_and_cannot_credit()
    {
        using var h = new Harness(); await h.Request(); h.Clock.Now = h.Clock.Now.AddHours(2);
        Assert.False(h.Scheduler(false).HasPaymentEvidence); Assert.Equal(0, await h.Scheduler(false).ScheduleAsync(default));
        Assert.Equal(RefundStatus.Pending, (await h.Read()).Status); Assert.Empty(h.Credits.Calls);
        await h.Scheduler().ScheduleAsync(default);
        await Assert.ThrowsAsync<RefundDependencyUnavailableException>(() => h.Completion(false).ProcessAsync(h.RefundId, 1, default));
        Assert.Empty(h.Credits.Calls);
    }
    [Fact]
    public async Task Completion_and_duplicate_due_messages_credit_actual_wallet_once()
    {
        using var h = new Harness(); await h.Request(); await h.Due();
        var consumer = new RefundDueConsumer(h.Completion()); var due = Assert.Single(h.Outbox.Events.OfType<RefundDueIntegrationEventV1>());
        await consumer.HandleAsync(MessageEnvelope.From(due, "x"), default);
        await consumer.HandleAsync(MessageEnvelope.From(due, "x"), default);
        await consumer.HandleAsync(MessageEnvelope.From(due with { EventId = Guid.NewGuid() }, "x"), default);
        var refund = await h.Read(); Assert.Equal(RefundStatus.Completed, refund.Status); Assert.NotNull(refund.ProcessedAtUtc);
        Assert.Equal(500_000m, (await h.Wallet.Queries.GetMyAsync(h.Wallet.Identity.User, default)).Balance);
        var ledger = Assert.Single(await h.Wallet.Db.Ledger.ToListAsync()); Assert.Equal(refund.IdempotencyKey, ledger.IdempotencyKey);
        Assert.Equal(ledger.Id, refund.WalletTransactionId); Assert.Equal("InventoryRefund", ledger.ReferenceType);
        Assert.Single(h.Outbox.Events.OfType<RefundCompletedIntegrationEventV1>()); Assert.Single(h.Credits.Calls);
        await h.Request(); Assert.Equal(RefundStatus.Completed, (await h.Read()).Status);
    }
    [Fact]
    public async Task Crash_after_wallet_commit_replays_same_credit_key_without_duplicate_ledger()
    {
        using var h = new Harness(); await h.Request(); await h.Due(); h.Store.FailCompletionOnce = true;
        await Assert.ThrowsAsync<IOException>(() => h.Completion().ProcessAsync(h.RefundId, 1, default));
        Assert.Equal(RefundStatus.Processing, (await h.Read()).Status); Assert.Single(await h.Wallet.Db.Ledger.ToListAsync());
        await h.Completion().ProcessAsync(h.RefundId, 1, default);
        Assert.Equal(RefundStatus.Completed, (await h.Read()).Status); Assert.Single(await h.Wallet.Db.Ledger.ToListAsync());
        Assert.Equal(h.Credits.Calls[0].Operation.IdempotencyKey, h.Credits.Calls[1].Operation.IdempotencyKey);
    }
    [Fact]
    public async Task Wallet_failure_preserves_retry_and_uses_new_delivery_with_same_business_key()
    {
        using var h = new Harness(); await h.Request(); await h.Due(); h.Credits.Failures = 1;
        await h.Completion().ProcessAsync(h.RefundId, 1, default); var refund = await h.Read();
        Assert.Equal(RefundStatus.Pending, refund.Status); Assert.Equal("WalletCreditFailed", refund.FailureCode);
        Assert.Null(refund.ProcessedAtUtc); Assert.Empty(await h.Wallet.Db.Ledger.ToListAsync());
        Assert.Equal(0, await h.Scheduler().ScheduleAsync(default));
        h.Clock.Now = refund.NextAttemptAtUtc; await h.Scheduler().ScheduleAsync(default);
        await h.Completion().ProcessAsync(h.RefundId, 1, default); Assert.Single(h.Credits.Calls); // obsolete retry
        await h.Completion().ProcessAsync(h.RefundId, 2, default); Assert.Equal(RefundStatus.Completed, (await h.Read()).Status);
        Assert.Equal(2, h.Outbox.Events.OfType<RefundDueIntegrationEventV1>().Select(value => value.EventId).Distinct().Count());
        Assert.Equal(h.Credits.Calls[0].Operation.IdempotencyKey, h.Credits.Calls[1].Operation.IdempotencyKey);
    }
    [Fact]
    public async Task Exhausted_wallet_failure_retains_failed_refund_and_safe_failure_event()
    {
        using var h = new Harness(); h.Options.MaximumAttempts = 1; await h.Request(); await h.Due(); h.Credits.Failures = 1;
        await h.Completion().ProcessAsync(h.RefundId, 1, default);
        Assert.Equal(RefundStatus.Failed, (await h.Read()).Status); Assert.Null((await h.Read()).ProcessedAtUtc);
        Assert.Equal("WalletCreditFailed", Assert.Single(h.Outbox.Events.OfType<RefundFailedIntegrationEventV1>()).FailureCode);
        Assert.Equal(0, await h.Scheduler().ScheduleAsync(default));
    }
    [Theory]
    [InlineData("owner")][InlineData("amount")][InlineData("currency")][InlineData("order")][InlineData("payment")]
    public async Task Evidence_mismatch_cannot_credit_any_wallet(string field)
    {
        using var h = new Harness(); await h.Request(); await h.Due(); var proof = h.Evidence.Proof!;
        h.Evidence.Proof = field switch { "owner" => proof with { UserId = h.Wallet.Identity.Other }, "amount" => proof with { Amount = 900_000m },
            "currency" => proof with { Currency = "USD" }, "order" => proof with { OrderId = Guid.NewGuid() }, _ => proof with { PaymentId = Guid.NewGuid() } };
        await h.Completion().ProcessAsync(h.RefundId, 1, default);
        Assert.Equal(RefundStatus.Failed, (await h.Read()).Status); Assert.Empty(h.Credits.Calls); Assert.Empty(await h.Wallet.Db.Wallets.ToListAsync());
    }
    [Fact]
    public async Task Unavailable_payment_evidence_is_retryable_and_not_completion()
    {
        using var h = new Harness(); await h.Request(); await h.Due(); h.Evidence.Proof = null;
        await h.Completion().ProcessAsync(h.RefundId, 1, default);
        Assert.Equal(RefundStatus.Pending, (await h.Read()).Status); Assert.Equal("PaymentEvidenceUnavailable", (await h.Read()).FailureCode);
        Assert.Empty(h.Credits.Calls);
    }
    [Fact]
    public void Domain_rejects_invalid_money_identity_and_transitions()
    {
        var clock = new Clock(); var payment = Guid.NewGuid(); var user = Guid.NewGuid(); var order = Guid.NewGuid();
        Assert.Throws<RefundRuleException>(() => RefundAggregate.Create(order, Guid.Empty, user, 100m, "IRR", clock.Now, TimeSpan.FromHours(1), "x"));
        Assert.ThrowsAny<Exception>(() => RefundAggregate.Create(order, payment, user, -1m, "IRR", clock.Now, TimeSpan.FromHours(1), "x"));
        Assert.ThrowsAny<Exception>(() => RefundAggregate.Create(order, payment, user, 1.5m, "IRR", clock.Now, TimeSpan.FromHours(1), "x"));
        var refund = RefundAggregate.Create(order, payment, user, 100m, "IRR", clock.Now, TimeSpan.FromHours(1), "x");
        Assert.Throws<RefundRuleException>(() => refund.Schedule(clock.Now)); Assert.Throws<RefundRuleException>(() => refund.Complete(Guid.NewGuid(), clock.Now));
        refund.Schedule(clock.Now.AddHours(1)); refund.Complete(Guid.NewGuid(), clock.Now.AddHours(1));
        Assert.Throws<RefundRuleException>(() => refund.Schedule(clock.Now.AddHours(2)));
        Assert.Throws<RefundRuleException>(() => refund.Retry(clock.Now, TimeSpan.FromMinutes(5), 8, "WalletCreditFailed"));
        Assert.True(new RefundProcessingOptions().IsValid()); Assert.False(new RefundProcessingOptions { DelayMinutes = -1 }.IsValid());
    }
    [Fact]
    public async Task Missing_order_invalid_messages_and_early_processing_do_not_credit()
    {
        using var h = new Harness(); h.Orders.Snapshot = null;
        await Assert.ThrowsAsync<InvalidMessageException>(() => h.Request()); Assert.Empty(await h.Db.Refunds.ToListAsync());
        h.Orders.Snapshot = new(h.Order, h.Wallet.Identity.User, 100m, "IRR"); await h.Request();
        await Assert.ThrowsAsync<InvalidMessageException>(() => h.Completion().ProcessAsync(h.RefundId, 1, default)); Assert.Empty(h.Credits.Calls);
    }
    [Fact]
    public async Task Refund_identity_and_completed_history_cannot_be_modified_or_deleted()
    {
        using var h = new Harness(); await h.Request(); var refund = await h.Read();
        h.Db.Entry(refund).Property(value => value.Amount).CurrentValue = 900_000m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync()); h.Db.ChangeTracker.Clear();
        refund = await h.Read(); h.Db.Refunds.Remove(refund); await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync());
        h.Db.ChangeTracker.Clear(); await h.Due(); await h.Completion().ProcessAsync(h.RefundId, 1, default); refund = await h.Read();
        h.Db.Entry(refund).Property(value => value.FailureCode).CurrentValue = "WalletCreditFailed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync());
    }
    [Fact(Skip = "Blocked: Phase 8 Payment producer and authoritative payment evidence adapter are absent. No fake Payment implementation is used.")]
    public void Payment_to_inventory_to_delayed_refund_to_wallet_end_to_end() { }
}
