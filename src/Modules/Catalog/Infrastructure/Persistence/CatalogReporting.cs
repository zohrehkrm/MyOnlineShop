using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Catalog.Domain;
using MyOnlineShop.Reporting.Contracts;

namespace MyOnlineShop.Catalog.Infrastructure.Persistence;

public sealed class CatalogReporting(CatalogDbContext db) : ICatalogReportSource
{
    public async Task<CatalogCounts> CountsAsync(CancellationToken ct) => await db.Products.AsNoTracking().GroupBy(x => 1)
        .Select(g => new CatalogCounts(g.LongCount(), g.LongCount(x => x.Status == ProductStatus.Active))).SingleOrDefaultAsync(ct) ?? new(0, 0);
}
