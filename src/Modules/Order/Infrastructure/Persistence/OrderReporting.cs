using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Order.Domain;
using MyOnlineShop.Reporting.Contracts;
using OrderEntity = MyOnlineShop.Order.Domain.Order;

namespace MyOnlineShop.Order.Infrastructure.Persistence;

public sealed class OrderReporting(OrderDbContext db) : IOrderReportSource
{
    private IQueryable<OrderEntity> Filter(ReportWindow window)
    {
        var values = db.Orders.AsNoTracking().Where(x => x.CreatedAtUtc >= window.FromUtc && x.CreatedAtUtc < window.ToUtc);
        if (window.Currency is not null) values = values.Where(x => x.Currency == window.Currency);
        if (window.OrderStatus is not null)
        {
            var status = Enum.Parse<OrderStatus>(window.OrderStatus);
            values = values.Where(x => x.Status == status);
        }
        return values;
    }
    private IQueryable<OrderEntity> Paid(ReportWindow window) => Filter(window).Where(x => x.Status == OrderStatus.Paid ||
        x.Status == OrderStatus.Processing || x.Status == OrderStatus.Shipped || x.Status == OrderStatus.Completed);
    public async Task<IReadOnlyList<StatusCount>> StatusesAsync(ReportWindow window, CancellationToken ct) =>
        await Filter(window).GroupBy(x => x.Status).OrderBy(g => g.Key).Select(g => new StatusCount(g.Key.ToString(), g.LongCount())).ToListAsync(ct);
    internal IQueryable<SalesCurrencyDto> SalesQuery(ReportWindow window) => Paid(window).GroupBy(x => x.Currency).OrderBy(g => g.Key)
        .Select(g => new SalesCurrencyDto(g.Key, g.LongCount(), g.Sum(x => x.Subtotal), g.Sum(x => x.DiscountTotal), g.Sum(x => x.ShippingCost),
            g.Sum(x => x.PayableAmount), g.Average(x => x.PayableAmount)));
    public async Task<IReadOnlyList<SalesCurrencyDto>> SalesAsync(ReportWindow window, CancellationToken ct) => await SalesQuery(window).ToListAsync(ct);
    internal IQueryable<SalesPeriodDto> PeriodQuery(ReportWindow window, string bucket) => Paid(window)
        .GroupBy(x => new { x.Currency, x.CreatedAtUtc.Year, x.CreatedAtUtc.Month, Day = bucket == "Month" ? 0 : x.CreatedAtUtc.Day })
        .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month).ThenBy(g => g.Key.Day).ThenBy(g => g.Key.Currency)
        .Select(g => new SalesPeriodDto(g.Key.Currency, g.Key.Year, g.Key.Month, g.Key.Day, g.LongCount(), g.Sum(x => x.PayableAmount)));
    public Task<ReportPage<SalesPeriodDto>> PeriodsAsync(ReportWindow window, string bucket, int page, int size, CancellationToken ct) =>
        PageAsync(PeriodQuery(window, bucket), page, size, ct);
    public Task<ReportPage<OrderReportRow>> OrdersAsync(ReportWindow window, int page, int size, CancellationToken ct) =>
        PageAsync(Filter(window).OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Select(x => new OrderReportRow(x.Id, x.Status.ToString(), x.Currency, x.PayableAmount, x.CreatedAtUtc)), page, size, ct);
    internal IQueryable<VariantSalesDto> VariantQuery(ReportWindow window) =>
        (from item in db.Items.AsNoTracking() join order in Paid(window) on item.OrderId equals order.Id
         select new { item.ProductVariantId, order.Currency, item.Quantity, item.LineTotal, item.OrderId })
        .GroupBy(x => new { x.ProductVariantId, x.Currency })
        .OrderByDescending(g => g.Sum(x => (long)x.Quantity)).ThenBy(g => g.Key.ProductVariantId).ThenBy(g => g.Key.Currency)
        .Select(g => new VariantSalesDto(g.Key.ProductVariantId, g.Key.Currency, g.Sum(x => (long)x.Quantity), g.Sum(x => x.LineTotal), g.Select(x => x.OrderId).Distinct().LongCount()));
    public Task<ReportPage<VariantSalesDto>> VariantsAsync(ReportWindow window, int page, int size, CancellationToken ct) =>
        PageAsync(VariantQuery(window), page, size, ct);
    internal IQueryable<BuyerSalesDto> BuyerQuery(ReportWindow window) => Paid(window).GroupBy(x => new { x.UserId, x.Currency })
        .OrderBy(g => g.Key.Currency).ThenByDescending(g => g.Sum(x => x.PayableAmount)).ThenBy(g => g.Key.UserId)
        .Select(g => new BuyerSalesDto(g.Key.UserId, g.Key.Currency, g.LongCount(), g.Sum(x => x.PayableAmount)));
    public Task<ReportPage<BuyerSalesDto>> BuyersAsync(ReportWindow window, int page, int size, CancellationToken ct) =>
        // Cross-currency ranking by money would be misleading. Rank within each currency instead.
        PageAsync(BuyerQuery(window), page, size, ct);
    private static async Task<ReportPage<T>> PageAsync<T>(IQueryable<T> query, int page, int size, CancellationToken ct) =>
        new(await query.Skip((page - 1) * size).Take(size).ToListAsync(ct), page, size, await query.LongCountAsync(ct));
}
