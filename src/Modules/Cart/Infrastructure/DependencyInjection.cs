using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Cart.Application;
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Cart.Infrastructure.Persistence;

namespace MyOnlineShop.Cart.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCartInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<CartDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(configuration.GetConnectionString("SqlServer"), sql =>
            {
                sql.CommandTimeout(settings.CommandTimeoutSeconds); sql.EnableRetryOnFailure(settings.MaxRetryCount);
                sql.MigrationsAssembly(typeof(CartDbContext).Assembly.FullName);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "cart");
            });
        });
        services.AddScoped<ICartRepository, CartRepository>(); services.AddScoped<ICartUnitOfWork, CartUnitOfWork>();
        services.AddScoped<ICartReadStore, CartReadStore>(); services.AddScoped<ICartQueries, GetCurrentCartHandler>();
        services.AddScoped<AddCartItemHandler>(); services.AddScoped<UpdateCartItemQuantityHandler>();
        services.AddScoped<RemoveCartItemHandler>(); services.AddScoped<ClearCartHandler>();
        services.AddScoped<ICartCommands, CartCommands>();
        return services;
    }
}
