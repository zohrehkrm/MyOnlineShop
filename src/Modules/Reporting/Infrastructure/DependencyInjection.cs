using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyOnlineShop.Reporting.Application;
using MyOnlineShop.Reporting.Contracts;

namespace MyOnlineShop.Reporting.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddReportingInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IReportingQueries, ReportingQueries>();
        return services;
    }
}
