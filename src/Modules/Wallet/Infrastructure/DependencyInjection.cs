using MyOnlineShop.Reporting.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Wallet.Application;
using MyOnlineShop.Wallet.Contracts;
using MyOnlineShop.Wallet.Infrastructure.Persistence;

namespace MyOnlineShop.Wallet.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddWalletInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<WalletDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(configuration.GetConnectionString("SqlServer"), sql =>
            {
                sql.CommandTimeout(settings.CommandTimeoutSeconds); sql.EnableRetryOnFailure(settings.MaxRetryCount);
                sql.MigrationsAssembly(typeof(WalletDbContext).Assembly.FullName); sql.MigrationsHistoryTable("__EFMigrationsHistory", "wallet");
            });
        });
        services.AddScoped<IWalletStore, WalletStore>(); services.AddScoped<IWalletUnitOfWork, WalletUnitOfWork>();
        services.AddScoped<IWalletQueries, WalletReadStore>(); services.AddScoped<IWalletOperations, WalletOperations>();
        services.AddScoped<IWalletAdministration, WalletAdministration>();
        services.AddScoped<IWalletReportSource, WalletReporting>();
        return services;
    }
}
