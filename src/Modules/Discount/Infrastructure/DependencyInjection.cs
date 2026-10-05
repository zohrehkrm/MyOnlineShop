using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Discount.Application;
using MyOnlineShop.Discount.Contracts;
using MyOnlineShop.Discount.Infrastructure.Persistence;

namespace MyOnlineShop.Discount.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddDiscountInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<DiscountDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(configuration.GetConnectionString("SqlServer"), sql =>
            {
                sql.CommandTimeout(settings.CommandTimeoutSeconds);
                sql.EnableRetryOnFailure(settings.MaxRetryCount);
                sql.MigrationsAssembly(typeof(DiscountDbContext).Assembly.FullName);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "discount");
            });
        });
        services.AddScoped<IDiscountStore, DiscountStore>();
        services.AddScoped<IDiscountUnitOfWork, DiscountUnitOfWork>();
        services.AddScoped<DiscountReadStore>();
        services.AddScoped<IDiscountQueries>(provider => provider.GetRequiredService<DiscountReadStore>());
        services.AddScoped<IDiscountCandidates>(provider => provider.GetRequiredService<DiscountReadStore>());
        services.AddScoped<IDiscountCommands, DiscountCommands>();
        return services;
    }
}
