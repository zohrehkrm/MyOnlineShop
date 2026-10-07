using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Reporting.Contracts;
using MyOnlineShop.Shipping.Domain;

namespace MyOnlineShop.Shipping.Infrastructure.Persistence;

public sealed class ShippingReporting(ShippingDbContext db) : IShippingReportSource
{
    public async Task<ShippingReportDto> SummaryAsync(ReportWindow window, string? status, int page, int size, CancellationToken ct)
    {
        var values = db.Shipments.AsNoTracking().Where(x => x.CreatedAtUtc >= window.FromUtc && x.CreatedAtUtc < window.ToUtc &&
            (window.Currency == null || x.Currency == window.Currency));
        if (status is not null) { var parsed = Enum.Parse<ShipmentStatus>(status); values = values.Where(x => x.Status == parsed); }
        var statuses = await values.GroupBy(x => x.Status).OrderBy(g => g.Key).Select(g => new ShippingStatusDto(g.Key.ToString(), g.LongCount())).ToListAsync(ct);
        var methods = values.GroupBy(x => x.ShippingMethodId).OrderByDescending(g => g.LongCount()).ThenBy(g => g.Key)
            .Select(g => new ShippingMethodSummaryDto(g.Key, g.LongCount()));
        return new(window, statuses, new(await methods
            .Skip((page - 1) * size).Take(size).ToListAsync(ct), page, size, await methods.LongCountAsync(ct)));
    }
}
