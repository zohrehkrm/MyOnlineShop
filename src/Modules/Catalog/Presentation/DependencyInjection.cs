using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Catalog.Infrastructure;
using MyOnlineShop.Identity.Contracts;

namespace MyOnlineShop.Catalog.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCatalogInfrastructure(configuration);
        services.AddScoped<CatalogAdministrationAudit>();
        services.AddControllers().AddApplicationPart(typeof(CatalogProductsController).Assembly);
        services.AddAuthorization(options => options.AddPolicy(IdentityPermissions.ManageCatalog,
            policy => policy.RequireAuthenticatedUser().RequireClaim("permission", IdentityPermissions.ManageCatalog)));
        return services;
    }
}
