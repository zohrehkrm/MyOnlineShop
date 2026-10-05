using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Catalog.Application;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Infrastructure.Persistence;

namespace MyOnlineShop.Catalog.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<CatalogDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(configuration.GetConnectionString("SqlServer"), sql =>
            {
                sql.CommandTimeout(settings.CommandTimeoutSeconds);
                sql.EnableRetryOnFailure(settings.MaxRetryCount);
                sql.MigrationsAssembly(typeof(CatalogDbContext).Assembly.FullName);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "catalog");
            });
        });
        services.AddScoped<ICatalogUnitOfWork, CatalogUnitOfWork>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IBrandRepository, BrandRepository>();
        services.AddScoped<IAttributeRepository, AttributeRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ICatalogReadStore, CatalogReadStore>();
        services.AddScoped<CategoryCommands>();
        services.AddScoped<TaxonomyCommands>();
        services.AddScoped<ProductCommands>();
        services.AddScoped<ICatalogCommands, CatalogCommands>();
        services.AddScoped<ICatalogQueries, CatalogQueries>();
        return services;
    }
}
