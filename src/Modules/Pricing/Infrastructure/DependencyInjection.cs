using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Application;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Pricing.Infrastructure.Persistence;

namespace MyOnlineShop.Pricing.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPricingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<PricingDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(configuration.GetConnectionString("SqlServer"), sql =>
            {
                sql.CommandTimeout(settings.CommandTimeoutSeconds);
                sql.EnableRetryOnFailure(settings.MaxRetryCount);
                sql.MigrationsAssembly(typeof(PricingDbContext).Assembly.FullName);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "pricing");
            });
        });
        services.AddScoped<IPriceStore, PriceStore>();
        services.AddScoped<IPriceUnitOfWork, PriceUnitOfWork>();
        services.AddScoped<IPriceReadStore, PriceReadStore>();
        services.AddScoped<IPriceQueries, PriceQueries>();
        services.AddScoped<IPriceCommands, PriceCommands>();
        services.AddScoped<IPricingCalculation, PricingCalculation>();
        return services;
    }
}
