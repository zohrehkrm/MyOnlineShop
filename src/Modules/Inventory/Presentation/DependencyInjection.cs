using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Inventory.Infrastructure;

namespace MyOnlineShop.Inventory.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddInventoryModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddInventoryInfrastructure(configuration);
        services.AddControllers().AddApplicationPart(typeof(InventoryController).Assembly);
        services.AddAuthorization(options =>
        {
            foreach (var permission in new[] { IdentityPermissions.ViewInventory, IdentityPermissions.ReceiveInventory,
                         IdentityPermissions.AdjustInventory, IdentityPermissions.DeductInventory })
                options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser().RequireClaim("permission", permission));
        });
        return services;
    }
}
