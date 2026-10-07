using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Domain;
using MyOnlineShop.Reporting.Contracts;

namespace MyOnlineShop.Identity.Infrastructure.Persistence;

public sealed class CustomerReporting(IdentityDbContext db) : ICustomerReportSource
{
    public async Task<CustomerCounts> CountsAsync(ReportWindow window, CancellationToken ct) => await db.Users.AsNoTracking()
        .Where(x => x.CreatedAtUtc >= window.FromUtc && x.CreatedAtUtc < window.ToUtc &&
            db.UserRoles.Any(role => role.UserId == x.Id && role.RoleId == IdentityPermissions.CustomerRoleId))
        .GroupBy(x => 1).Select(g => new CustomerCounts(g.LongCount(), g.LongCount(x => x.Status == UserStatus.Active)))
        .SingleOrDefaultAsync(ct) ?? new(0, 0);
}
