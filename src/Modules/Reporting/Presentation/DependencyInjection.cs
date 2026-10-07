using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Reporting.Infrastructure;

namespace MyOnlineShop.Reporting.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        services.AddReportingInfrastructure(); services.AddControllers().AddApplicationPart(typeof(ReportsController).Assembly);
        services.AddAuthorization(options =>
        {
            foreach (var permission in new[] { IdentityPermissions.ViewReports, IdentityPermissions.ReportSales,
                IdentityPermissions.ReportInventory, IdentityPermissions.ReportCustomers, IdentityPermissions.ReportFinancial })
                options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser().RequireClaim("permission", permission));
        });
        return services;
    }
}
