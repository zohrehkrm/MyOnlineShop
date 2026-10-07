using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Reporting.Contracts;
using MyOnlineShop.Wallet.Domain;

namespace MyOnlineShop.Wallet.Infrastructure.Persistence;

public sealed class WalletReporting(WalletDbContext db) : IWalletReportSource
{
    internal IQueryable<WalletSummaryDto> SummaryQuery(ReportWindow window) => db.Ledger.AsNoTracking()
        .Where(x => x.Status == WalletTransactionStatus.Posted && x.CreatedAtUtc >= window.FromUtc && x.CreatedAtUtc < window.ToUtc &&
            (window.Currency == null || x.Currency == window.Currency))
        .GroupBy(x => x.Currency).OrderBy(g => g.Key).Select(g => new WalletSummaryDto(g.Key, g.LongCount(),
            g.Sum(x => x.Type == WalletTransactionType.Credit ? x.Amount : 0m),
            g.Sum(x => x.Type == WalletTransactionType.Debit ? -x.Amount : 0m),
            g.Sum(x => x.Type == WalletTransactionType.Credit && x.ReferenceType == "InventoryRefund" ? x.Amount : 0m)));
    public async Task<IReadOnlyList<WalletSummaryDto>> SummaryAsync(ReportWindow window, CancellationToken ct) => await SummaryQuery(window).ToListAsync(ct);
}
