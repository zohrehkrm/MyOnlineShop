using MyOnlineShop.Reporting.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Order.Application;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Order.Infrastructure.Persistence;

namespace MyOnlineShop.Order.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<OrderDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(configuration.GetConnectionString("SqlServer"), sql =>
            {
                sql.CommandTimeout(settings.CommandTimeoutSeconds); sql.EnableRetryOnFailure(settings.MaxRetryCount);
                sql.MigrationsAssembly(typeof(OrderDbContext).Assembly.FullName);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "ordering");
            });
        });
        services.AddScoped<IOrderStore, OrderStore>(); services.AddScoped<IOrderUnitOfWork, OrderUnitOfWork>();
        services.AddScoped<IOrderReadStore, OrderReadStore>();
        services.AddScoped<IOrderQueries>(provider => provider.GetRequiredService<IOrderReadStore>());
        services.AddScoped<IOrderCommands, OrderCommands>(); services.AddScoped<ICheckoutCommands, CheckoutCommands>();
        services.AddScoped<IOrderRefundSnapshots, OrderRefundSnapshots>();
        services.AddScoped<IOrderShippingSnapshots, OrderShippingSnapshots>();
        services.AddScoped<IOrderReportSource, OrderReporting>();
        return services;
    }
}
