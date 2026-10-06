using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Wallet.Application;
using MyOnlineShop.Wallet.Contracts;
using MyOnlineShop.Wallet.Domain;
using Xunit;

namespace MyOnlineShop.Wallet.Tests;

public sealed class WalletTests
{
    [Fact]
    public async Task Equivalent_decimal_scale_currency_and_description_normalize_to_same_operation()
    {
        using var h = new Harness(); var input = h.Input(100m); var first = await h.Credit(input);
        var equivalent = new WalletOperation { IdempotencyKey = input.IdempotencyKey, Amount = 100.00m, Currency = " irr ",
            ReferenceType = input.ReferenceType, ReferenceId = input.ReferenceId, Description = " " + input.Description + " " };
        Assert.Equal(first, await h.Credit(equivalent)); Assert.Single(await h.Db.Ledger.ToListAsync());
        Assert.Equal(100m, (await h.Queries.GetMyAsync(h.Identity.User, default)).Balance);
    }
    [Fact]
    public async Task Invalid_keys_references_description_and_actor_are_rejected_before_posting()
    {
        using var h = new Harness();
        foreach (var input in new WalletOperation[]
        {
            h.Input(key: Guid.Empty), h.Input(reference: ""), h.Input(reference: new string('R', 129)),
            new() { IdempotencyKey = Guid.NewGuid(), Amount = 1m, Currency = "IRR", ReferenceType = "Bad Type", ReferenceId = "1", Description = "Test" },
            new() { IdempotencyKey = Guid.NewGuid(), Amount = 1m, Currency = "IRR", ReferenceType = "Test", ReferenceId = "1", Description = " " },
            new() { IdempotencyKey = Guid.NewGuid(), Amount = 1m, Currency = "IRR", ReferenceType = "Test", ReferenceId = "1", Description = new string('D', 501) }
        }) await Assert.ThrowsAsync<WalletException>(() => h.Credit(input));
        await Assert.ThrowsAsync<WalletException>(() => h.Operations.CreditAsync(h.Identity.User, Guid.Empty, h.Input(), default));
        Assert.Empty(await h.Db.Wallets.ToListAsync()); Assert.Empty(await h.Db.Ledger.ToListAsync());
    }
    [Fact]
    public async Task Exact_balance_debit_reaches_zero_and_failed_key_can_be_retried_after_credit()
    {
        using var h = new Harness(); await h.Credit(h.Input(100m)); var input = h.Input(200m);
        await Assert.ThrowsAsync<WalletException>(() => h.Debit(input)); Assert.Single(await h.Db.Ledger.ToListAsync());
        await h.Credit(h.Input(100m)); var debit = await h.Debit(input);
        Assert.Equal(200m, debit.BalanceBefore); Assert.Equal(0m, debit.BalanceAfter); Assert.Equal(-200m, debit.Amount);
        Assert.Equal(debit, await h.Debit(input)); Assert.Equal(3, await h.Db.Ledger.CountAsync());
    }
    [Fact]
    public async Task Valid_credit_and_debit_post_signed_immutable_history_and_materialized_balance()
    {
        using var h = new Harness();
        var credit = await h.Credit(); Assert.Equal(500_000m, credit.Amount);
        Assert.Equal(0m, credit.BalanceBefore); Assert.Equal(500_000m, credit.BalanceAfter); Assert.Equal("Credit", credit.Type); Assert.Equal("Posted", credit.Status);
        var debit = await h.Debit(); Assert.Equal(-200_000m, debit.Amount); Assert.Equal("Debit", debit.Type);
        Assert.Equal(500_000m, debit.BalanceBefore); Assert.Equal(300_000m, debit.BalanceAfter);
        Assert.Equal(300_000m, (await h.Queries.GetMyAsync(h.Identity.User, default)).Balance);
        Assert.Single(await h.Db.Wallets.ToListAsync()); Assert.Equal(2, await h.Db.Ledger.CountAsync());
        Assert.Equal(300_000m, await h.Db.Ledger.SumAsync(value => value.Amount));
        Assert.Equal(h.Actor, debit.ActorId); Assert.Equal("wallet-test", debit.CorrelationId); Assert.Equal(TimeSpan.Zero, debit.CreatedAtUtc.Offset);
    }
    [Fact]
    public async Task Repeated_credit_and_debit_return_original_operations_after_subsequent_balance_changes()
    {
        using var h = new Harness(); var input = h.Input(1_000_000m); var first = await h.Credit(input);
        Assert.Equal(first, await h.Credit(input)); Assert.Single(await h.Db.Ledger.ToListAsync());
        var debitInput = h.Input(700_000m); var debit = await h.Debit(debitInput);
        Assert.Equal(debit, await h.Debit(debitInput));
        Assert.Equal(first, await h.Credit(input)); Assert.Equal(300_000m, (await h.Queries.GetMyAsync(h.Identity.User, default)).Balance);
        Assert.Equal(2, await h.Db.Ledger.CountAsync());
    }
    [Fact]
    public async Task Changed_key_payload_actor_direction_or_duplicate_reference_is_rejected()
    {
        using var h = new Harness(); var input = h.Input(); await h.Credit(input);
        await Assert.ThrowsAsync<WalletException>(() => h.Credit(h.Input(600_000m, input.IdempotencyKey, reference: input.ReferenceId)));
        await Assert.ThrowsAsync<WalletException>(() => h.Debit(input));
        await Assert.ThrowsAsync<WalletException>(() => h.Operations.CreditAsync(h.Identity.User, Guid.NewGuid(), input, default));
        await Assert.ThrowsAsync<WalletException>(() => h.Credit(h.Input(reference: input.ReferenceId)));
        Assert.Equal(500_000m, (await h.Queries.GetMyAsync(h.Identity.User, default)).Balance); Assert.Single(await h.Db.Ledger.ToListAsync());
    }
    [Theory]
    [InlineData("IRR", 0)]
    [InlineData("IRR", -1)]
    [InlineData("IRR", 1.5)]
    [InlineData("USD", 1.001)]
    [InlineData("USD", 1000000000001)]
    [InlineData("XYZ", 10)]
    public async Task Invalid_credit_and_debit_amounts_or_currency_are_rejected(string currency, decimal amount)
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<WalletException>(() => h.Credit(h.Input(amount, currency: currency)));
        await Assert.ThrowsAsync<WalletException>(() => h.Debit(h.Input(amount, currency: currency)));
        Assert.Empty(await h.Db.Wallets.ToListAsync()); Assert.Empty(await h.Db.Ledger.ToListAsync());
    }
    [Fact]
    public async Task Insufficient_balance_missing_wallet_and_unknown_owner_post_nothing()
    {
        using var h = new Harness();
        Assert.Equal(404, (await Assert.ThrowsAsync<WalletException>(() => h.Debit())).StatusCode);
        await h.Credit(h.Input(1_000_000m)); await h.Debit(h.Input(700_000m));
        Assert.Equal(409, (await Assert.ThrowsAsync<WalletException>(() => h.Debit(h.Input(700_000m)))).StatusCode);
        Assert.Equal(300_000m, (await h.Queries.GetMyAsync(h.Identity.User, default)).Balance); Assert.Equal(2, await h.Db.Ledger.CountAsync());
        await Assert.ThrowsAsync<WalletException>(() => h.Operations.CreditAsync(Guid.NewGuid(), h.Actor, h.Input(), default));
        Assert.Single(await h.Db.Wallets.ToListAsync());
    }
    [Fact]
    public async Task One_primary_wallet_has_immutable_currency_and_bounded_balance()
    {
        using var h = new Harness(); await h.Credit(h.Input(10.25m, currency: "USD")); await h.Credit(h.Input(5m, currency: "USD"));
        Assert.Single(await h.Db.Wallets.ToListAsync());
        Assert.Equal(15.25m, (await h.Queries.GetMyAsync(h.Identity.User, default)).Balance);
        await Assert.ThrowsAsync<WalletException>(() => h.Credit(h.Input(currency: "IRR")));
        await Assert.ThrowsAsync<WalletException>(() => h.Credit(h.Input(MoneyRules.MaximumAmount, currency: "USD")));
    }
    [Fact]
    public async Task Owner_queries_are_read_only_and_ledger_supports_bounded_filters()
    {
        using var h = new Harness(); await h.Credit(); await h.Debit();
        await Assert.ThrowsAsync<WalletException>(() => h.Queries.GetMyAsync(h.Identity.Other, default));
        await Assert.ThrowsAsync<WalletException>(() => h.Queries.GetMyTransactionsAsync(h.Identity.Other, new(), default));
        var debit = await h.Queries.GetMyTransactionsAsync(h.Identity.User, new() { Type = "Debit", PageSize = 1 }, default);
        Assert.Single(debit.Items); Assert.Equal(1, debit.TotalCount); Assert.Equal("Debit", debit.Items[0].Type);
        Assert.Empty((await h.Queries.GetMyTransactionsAsync(h.Identity.User, new() { FromUtc = FixedClock.Now.AddDays(1) }, default)).Items);
        await Assert.ThrowsAsync<WalletException>(() => h.Queries.GetMyTransactionsAsync(h.Identity.User, new() { PageSize = 101 }, default));
        await Assert.ThrowsAsync<WalletException>(() => h.Queries.GetMyTransactionsAsync(h.Identity.User, new() { FromUtc = FixedClock.Now, ToUtc = FixedClock.Now }, default));
        Assert.Equal(2, await h.Db.Ledger.CountAsync());
    }
    [Fact]
    public async Task EF_rejects_ledger_edits_deletes_unpaired_balance_changes_and_orphan_postings()
    {
        using var h = new Harness(); await h.Credit(); h.Db.ResetPosting();
        var ledger = await h.Db.Ledger.SingleAsync(); h.Db.Entry(ledger).Property(value => value.Description).CurrentValue = "Rewritten";
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync());
        h.Db.ResetPosting(); h.Db.Ledger.Remove(await h.Db.Ledger.SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync());
        h.Db.ResetPosting(); var wallet = await h.Db.Wallets.SingleAsync(); h.Db.Entry(wallet).Property(value => value.Balance).CurrentValue = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync());
        h.Db.ResetPosting(); h.Db.RecordBalanceChange(new(wallet.Id, 500_000m, 600_000m));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync());
        h.Db.ResetPosting(); var input = h.Input(100m);
        h.Db.Ledger.Add(WalletLedger.Post(new(wallet.Id, 500_000m, 500_100m), WalletTransactionType.Credit, input, new string('A', 64), h.Actor, FixedClock.Now, "test"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync());
    }
}
