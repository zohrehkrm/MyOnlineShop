using MyOnlineShop.Reporting.Application;
using MyOnlineShop.Reporting.Contracts;
using Xunit;

namespace MyOnlineShop.Reporting.Tests;

public sealed class ReportingTests
{
    [Fact]
    public async Task Dashboard_aggregates_owned_sources_without_tracking_or_mixing_currency()
    {
        using var h = new Harness(); await h.SeedAsync(); var result = await h.Queries.DashboardAsync(Harness.Request(), default);
        Assert.Equal(5, result.OrderStatuses.Sum(x => x.Count)); Assert.Equal(1, result.OrderStatuses.Single(x => x.Status == "Paid").Count);
        Assert.Equal(2, result.CustomerRegistrations.RegisteredCustomers); Assert.Equal(1, result.CustomerRegistrations.ActiveRegisteredCustomers);
        Assert.Equal(1, result.Catalog.CurrentProducts); Assert.Equal(1, result.CurrentLowStockVariants); Assert.False(result.PaymentReportsAvailable);
        Assert.Equal(330m, result.RecordedSales.Single(x => x.Currency == "IRR").RecordedPaidOrderValue);
        Assert.Equal(6m, result.RecordedSales.Single(x => x.Currency == "USD").RecordedPaidOrderValue);
        Assert.Contains("not verified Payment", result.SalesBasis); Assert.All(h.Contexts, db => Assert.Empty(db.ChangeTracker.Entries()));
    }
    [Fact]
    public async Task Sales_use_historical_amounts_paid_statuses_and_half_open_utc_window()
    {
        using var h = new Harness(); await h.SeedAsync();
        var result = await h.Queries.SalesAsync(new() { FromUtc = Harness.From, ToUtc = Harness.To, Currency = "irr" }, default);
        var money = Assert.Single(result.Totals); Assert.Equal("IRR", money.Currency); Assert.Equal(2, money.OrderCount);
        Assert.Equal(350m, money.Subtotal); Assert.Equal(20m, money.Discounts); Assert.Equal(330m, money.RecordedPaidOrderValue); Assert.Equal(165m, money.AverageOrderValue);
        Assert.Equal(2, result.Periods.TotalCount); Assert.Equal(new[] { 1, 2 }, result.Periods.Items.Select(x => x.Day));
    }
    [Fact]
    public async Task Monthly_sales_and_status_filters_are_applied_before_aggregation()
    {
        using var h = new Harness(); await h.SeedAsync();
        var month = await h.Queries.SalesAsync(new() { FromUtc = Harness.From, ToUtc = Harness.To, Bucket = "Month", Currency = "IRR" }, default);
        Assert.Equal(0, Assert.Single(month.Periods.Items).Day); Assert.Equal(330m, month.Periods.Items[0].RecordedPaidOrderValue);
        var completed = await h.Queries.SalesAsync(new() { FromUtc = Harness.From, ToUtc = Harness.To, OrderStatus = "Completed" }, default);
        Assert.Equal(150m, Assert.Single(completed.Totals).RecordedPaidOrderValue);
        Assert.Empty((await h.Queries.SalesAsync(new() { FromUtc = Harness.From, ToUtc = Harness.To, OrderStatus = "Cancelled" }, default)).Totals);
    }
    [Fact]
    public async Task Order_lists_are_bounded_stable_and_preserve_status_counts()
    {
        using var h = new Harness(); await h.SeedAsync();
        var first = await h.Queries.OrdersAsync(Harness.Request(size: 2), default); var second = await h.Queries.OrdersAsync(Harness.Request(2, 2), default);
        Assert.Equal(5, first.Orders.TotalCount); Assert.Equal(2, first.Orders.Items.Count); Assert.Empty(first.Orders.Items.Select(x => x.OrderId).Intersect(second.Orders.Items.Select(x => x.OrderId)));
        var cancelled = await h.Queries.OrdersAsync(Harness.Request(status: "Cancelled"), default);
        Assert.Single(cancelled.Orders.Items); Assert.Equal(1, Assert.Single(cancelled.Statuses).Count);
    }
    [Fact]
    public async Task Variant_sales_use_order_items_not_current_catalog_or_prices()
    {
        using var h = new Harness(); await h.SeedAsync(); var rows = await h.Queries.ProductsAsync(Harness.Request(), default);
        Assert.Equal(2, rows.TotalCount); var irr = rows.Items.Single(x => x.Currency == "IRR");
        Assert.Equal(h.Variant, irr.ProductVariantId); Assert.Equal(3, irr.Quantity); Assert.Equal(330m, irr.NetItemSales); Assert.Equal(2, irr.OrderFrequency);
    }
    [Fact]
    public async Task Customers_exclude_admin_registrations_and_spending_stays_by_currency_without_pii()
    {
        using var h = new Harness(); await h.SeedAsync(); var result = await h.Queries.CustomersAsync(Harness.Request(), default);
        Assert.Equal(2, result.Registrations.RegisteredCustomers); Assert.Equal(1, result.Registrations.ActiveRegisteredCustomers);
        Assert.Equal(330m, result.Buyers.Items.Single(x => x.UserId == h.BuyerA).RecordedPaidOrderValue);
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("example.com", json); Assert.DoesNotContain("Password", json); Assert.DoesNotContain("hash", json);
    }
    [Fact]
    public async Task Inventory_reports_distinct_low_stock_variants_and_signed_movement_totals()
    {
        using var h = new Harness(); await h.SeedAsync();
        var result = await h.Queries.InventoryAsync(new() { FromUtc = Harness.From, ToUtc = Harness.To, WarehouseId = h.Warehouse, LowStockOnly = true }, default);
        Assert.Equal(1, result.CurrentLowStockVariants); Assert.Equal(2, Assert.Single(result.CurrentStock.Items).Quantity);
        Assert.Equal(10, result.Movements.Items.Single(x => x.Type == "Receipt").QuantityAdded);
        Assert.Equal(8, result.Movements.Items.Single(x => x.Type == "Sale").QuantityDeducted);
        Assert.Equal(-8, result.Movements.Items.Single(x => x.Type == "Sale").NetQuantity);
        Assert.Equal(7, result.Movements.Items.Single(x => x.Type == "ManualAdjustment").QuantityAdded);
        Assert.Empty((await h.Queries.InventoryAsync(new() { FromUtc = Harness.From, ToUtc = Harness.To, WarehouseId = Guid.NewGuid() }, default)).CurrentStock.Items);
    }
    [Fact]
    public async Task Wallet_totals_come_only_from_posted_ledger_with_positive_debit_and_refund_subtotal()
    {
        using var h = new Harness(); await h.SeedAsync(); var result = await h.Queries.WalletAsync(Harness.Request(), default);
        Assert.Equal(2, result.Count); var irr = result.Single(x => x.Currency == "IRR");
        Assert.Equal(3, irr.TransactionCount); Assert.Equal(110m, irr.Credits); Assert.Equal(25m, irr.Debits); Assert.Equal(10m, irr.InventoryRefundCredits);
        Assert.Equal(3m, Assert.Single(await h.Queries.WalletAsync(Harness.Request(currency: "USD"), default)).Credits);
    }
    [Fact]
    public async Task Shipping_aggregates_actual_status_and_method_with_bounded_results()
    {
        using var h = new Harness(); await h.SeedAsync(); var result = await h.Queries.ShippingAsync(new() { FromUtc = Harness.From, ToUtc = Harness.To }, default);
        Assert.Equal(2, result.Statuses.Sum(x => x.Count)); Assert.Equal(1, result.Statuses.Single(x => x.Status == "Delivered").Count); Assert.Equal(2, Assert.Single(result.Methods.Items).Count);
        Assert.Equal(1, (await h.Queries.ShippingAsync(new() { FromUtc = Harness.From, ToUtc = Harness.To, ShipmentStatus = "Pending" }, default)).Statuses.Sum(x => x.Count));
    }
    [Fact]
    public async Task Empty_datasets_return_empty_pages_and_zero_counts()
    {
        using var h = new Harness(); var dashboard = await h.Queries.DashboardAsync(Harness.Request(), default);
        Assert.Empty(dashboard.OrderStatuses); Assert.Empty(dashboard.RecordedSales); Assert.Equal(0, dashboard.Catalog.CurrentProducts); Assert.Equal(0, dashboard.CurrentLowStockVariants);
        Assert.Empty((await h.Queries.OrdersAsync(Harness.Request(), default)).Orders.Items);
        Assert.Empty((await h.Queries.ProductsAsync(Harness.Request(), default)).Items);
        Assert.Empty(await h.Queries.WalletAsync(Harness.Request(), default));
        Assert.Empty((await h.Queries.ShippingAsync(new() { FromUtc = Harness.From, ToUtc = Harness.To }, default)).Statuses);
    }
    [Theory]
    [InlineData(0, 20)] [InlineData(1, 101)] [InlineData(int.MaxValue, 100)]
    public async Task Invalid_pagination_is_rejected(int page, int size)
    { using var h = new Harness(); await Assert.ThrowsAsync<ReportingException>(() => h.Queries.OrdersAsync(Harness.Request(page, size), default)); }
    [Fact]
    public async Task Invalid_dates_status_currency_and_buckets_are_rejected()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<ReportingException>(() => h.Queries.OrdersAsync(new() { FromUtc = Harness.To, ToUtc = Harness.From }, default));
        await Assert.ThrowsAsync<ReportingException>(() => h.Queries.OrdersAsync(new() { ToUtc = DateTimeOffset.MinValue }, default));
        await Assert.ThrowsAsync<ReportingException>(() => h.Queries.OrdersAsync(new() { ToUtc = DateTimeOffset.MinValue.AddDays(15) }, default));
        await Assert.ThrowsAsync<ReportingException>(() => h.Queries.OrdersAsync(new() { FromUtc = Harness.From, ToUtc = Harness.From.AddDays(367) }, default));
        await Assert.ThrowsAsync<ReportingException>(() => h.Queries.OrdersAsync(Harness.Request(status: "1"), default));
        await Assert.ThrowsAsync<ReportingException>(() => h.Queries.OrdersAsync(Harness.Request(currency: "INVALID"), default));
        await Assert.ThrowsAsync<ReportingException>(() => h.Queries.SalesAsync(new() { Bucket = "Year" }, default));
        await Assert.ThrowsAsync<ReportingException>(() => h.Queries.WalletAsync(Harness.Request(status: "Paid"), default));
        await Assert.ThrowsAsync<ReportingException>(() => h.Queries.InventoryAsync(new() { Currency = "USD" }, default));
        await Assert.ThrowsAsync<ReportingException>(() => h.Queries.ShippingAsync(new() { ShipmentStatus = "7" }, default));
    }
    [Fact]
    public void Date_offsets_are_normalized_and_default_window_is_bounded()
    {
        var window = ReportValidation.Window(new() { FromUtc = Harness.From.ToOffset(TimeSpan.FromHours(3.5)), ToUtc = Harness.To }, new FixedClock());
        Assert.Equal(TimeSpan.Zero, window.FromUtc.Offset); Assert.Equal(Harness.From, window.FromUtc);
        Assert.Equal(TimeSpan.FromDays(30), ReportValidation.Window(new(), new FixedClock()).ToUtc - ReportValidation.Window(new(), new FixedClock()).FromUtc);
    }
    [Fact]
    public async Task Cancellation_reaches_owner_queries_without_tracking_or_writes()
    {
        using var h = new Harness(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Queries.DashboardAsync(Harness.Request(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Queries.WalletAsync(Harness.Request(), cancelled.Token));
        Assert.All(h.Contexts, db => Assert.Empty(db.ChangeTracker.Entries()));
    }
}
