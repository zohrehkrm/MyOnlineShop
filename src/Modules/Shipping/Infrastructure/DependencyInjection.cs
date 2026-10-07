using MyOnlineShop.Reporting.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Shipping.Application;
using MyOnlineShop.Shipping.Contracts;
using MyOnlineShop.Shipping.Infrastructure.Persistence;

namespace MyOnlineShop.Shipping.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddShippingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<ShippingDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(configuration.GetConnectionString("SqlServer"), sql =>
            {
                sql.CommandTimeout(settings.CommandTimeoutSeconds); sql.EnableRetryOnFailure(settings.MaxRetryCount);
                sql.MigrationsAssembly(typeof(ShippingDbContext).Assembly.FullName); sql.MigrationsHistoryTable("__EFMigrationsHistory", "shipping");
            });
        });
        services.AddScoped<IShippingStore, ShippingStore>(); services.AddScoped<IShippingUnitOfWork, ShippingUnitOfWork>();
        services.AddScoped<IShippingCommands, ShippingCommands>(); services.AddScoped<IShippingQueries, ShippingQueries>(); services.AddScoped<IShippingQuotes, ShippingQuotes>();
        services.AddScoped<IShippingReportSource, ShippingReporting>();
        return services;
    }
}
