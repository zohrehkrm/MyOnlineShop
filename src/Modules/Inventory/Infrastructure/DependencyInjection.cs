using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Inventory.Application;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;

namespace MyOnlineShop.Inventory.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInventoryInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<InventoryDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(configuration.GetConnectionString("SqlServer"), sql =>
            {
                sql.CommandTimeout(settings.CommandTimeoutSeconds); sql.EnableRetryOnFailure(settings.MaxRetryCount);
                sql.MigrationsAssembly(typeof(InventoryDbContext).Assembly.FullName);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "inventory");
            });
        });
        services.AddScoped<IInventoryUnitOfWork, InventoryUnitOfWork>();
        services.AddScoped<IInventoryStore, InventoryStore>();
        services.AddScoped<IInventoryReadStore, InventoryReadStore>();
        services.AddScoped<IInventoryQueries, InventoryQueries>();
        services.AddScoped<IInventoryAvailability, InventoryAvailability>();
        services.AddScoped<WarehouseCommands>(); services.AddScoped<StockCommands>(); services.AddScoped<InventoryCommands>();
        services.AddScoped<IInventoryCommands>(provider => provider.GetRequiredService<InventoryCommands>());
        services.AddScoped<IInventoryDeduction>(provider => provider.GetRequiredService<InventoryCommands>());
        services.AddScoped<IMessageConsumer, PaymentSucceededConsumer>();
        services.AddScoped<ILocalSqlTransactionParticipant>(provider => new LocalSqlTransactionParticipant<InventoryDbContext>(provider.GetRequiredService<InventoryDbContext>(), "inventory"));
        return services;
    }
}
