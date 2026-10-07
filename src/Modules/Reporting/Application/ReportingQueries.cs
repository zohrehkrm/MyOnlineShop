using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Reporting.Contracts;

namespace MyOnlineShop.Reporting.Application;

public sealed class ReportingException(string message) : Exception(message), IApplicationError
{
    public string Code => "report_validation";
    public int StatusCode => 400;
    public string SafeMessage => Message;
}
public static class ReportValidation
{
    public static ReportWindow Window(ReportRequest query, TimeProvider clock, bool orderStatusAllowed = true, bool currencyAllowed = true)
    {
        var to = (query.ToUtc ?? clock.GetUtcNow()).ToUniversalTime();
        if (query.FromUtc is null && to < DateTimeOffset.MinValue.AddDays(30))
            throw new ReportingException("ToUtc is too early for the default 30-day interval.");
        var from = (query.FromUtc ?? to.AddDays(-30)).ToUniversalTime();
        if (from == default || from >= to || to - from > TimeSpan.FromDays(366))
            throw new ReportingException("Specify a nonempty UTC interval no longer than 366 days; ToUtc is exclusive.");
        if (query.Page < 1 || query.PageSize is < 1 or > 100 || ((long)query.Page - 1) * query.PageSize > int.MaxValue)
            throw new ReportingException("Page must be positive and page size between 1 and 100.");
        if (query.OrderStatus is not null && (!orderStatusAllowed || query.OrderStatus is not
            ("Pending" or "AwaitingPayment" or "Paid" or "Processing" or "Shipped" or "Completed" or "Cancelled" or "Failed")))
            throw new ReportingException("Order status is unsupported for this report.");
        if (!currencyAllowed && query.Currency is not null) throw new ReportingException("Currency does not apply to this report.");
        string? currency = null;
        if (query.Currency is not null)
        {
            try { currency = MoneyRules.Currency(query.Currency); } catch (MoneyRuleException error) { throw new ReportingException(error.Message); }
        }
        return new(from, to, currency, query.OrderStatus);
    }
}

public sealed class ReportingQueries(IOrderReportSource orders, IInventoryReportSource inventory, ICustomerReportSource customers,
    ICatalogReportSource catalog, IWalletReportSource wallet, IShippingReportSource shipping, TimeProvider clock) : IReportingQueries
{
    public const string SalesBasis = "Recorded Order status Paid/Processing/Shipped/Completed; creation-date cohorts, not verified Payment receipts.";
    public async Task<DashboardDto> DashboardAsync(ReportRequest query, CancellationToken ct)
    {
        var window = ReportValidation.Window(query, clock);
        return new(window, await orders.StatusesAsync(window, ct), await orders.SalesAsync(window, ct),
            await customers.CountsAsync(window, ct), await catalog.CountsAsync(ct), await inventory.LowStockVariantsAsync(null, ct), SalesBasis, false);
    }
    public async Task<SalesReportDto> SalesAsync(SalesRequest query, CancellationToken ct)
    {
        var window = ReportValidation.Window(query, clock);
        if (query.Bucket is not ("Day" or "Month")) throw new ReportingException("Bucket must be Day or Month.");
        return new(window, SalesBasis, await orders.SalesAsync(window, ct), await orders.PeriodsAsync(window, query.Bucket, query.Page, query.PageSize, ct));
    }
    public async Task<OrderReportDto> OrdersAsync(ReportRequest query, CancellationToken ct)
    {
        var window = ReportValidation.Window(query, clock);
        return new(window, await orders.StatusesAsync(window, ct), await orders.OrdersAsync(window, query.Page, query.PageSize, ct));
    }
    public Task<ReportPage<VariantSalesDto>> ProductsAsync(ReportRequest query, CancellationToken ct) =>
        orders.VariantsAsync(ReportValidation.Window(query, clock), query.Page, query.PageSize, ct);
    public async Task<InventoryReportDto> InventoryAsync(InventoryRequest query, CancellationToken ct)
    {
        var window = ReportValidation.Window(query, clock, false, false);
        if (query.WarehouseId == Guid.Empty) throw new ReportingException("Warehouse ID cannot be empty.");
        return new(window, await inventory.LowStockVariantsAsync(query.WarehouseId, ct),
            await inventory.StockAsync(query.WarehouseId, query.LowStockOnly, query.Page, query.PageSize, ct),
            await inventory.MovementsAsync(window, query.WarehouseId, query.Page, query.PageSize, ct));
    }
    public async Task<CustomerReportDto> CustomersAsync(ReportRequest query, CancellationToken ct)
    {
        var window = ReportValidation.Window(query, clock);
        return new(window, await customers.CountsAsync(window, ct), await orders.BuyersAsync(window, query.Page, query.PageSize, ct));
    }
    public Task<IReadOnlyList<WalletSummaryDto>> WalletAsync(ReportRequest query, CancellationToken ct) =>
        wallet.SummaryAsync(ReportValidation.Window(query, clock, false), ct);
    public Task<ShippingReportDto> ShippingAsync(ShippingRequest query, CancellationToken ct)
    {
        var window = ReportValidation.Window(query, clock, false);
        if (query.ShipmentStatus is not null && query.ShipmentStatus is not ("Pending" or "Preparing" or "Shipped" or "InTransit" or "Delivered" or "Cancelled"))
            throw new ReportingException("Shipment status is invalid.");
        return shipping.SummaryAsync(window, query.ShipmentStatus, query.Page, query.PageSize, ct);
    }
}
