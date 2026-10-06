using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Shipping.Infrastructure;

namespace MyOnlineShop.Shipping.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddShippingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddShippingInfrastructure(configuration); services.AddControllers().AddApplicationPart(typeof(ShippingController).Assembly);
        services.AddAuthorization(options =>
        {
            foreach (var permission in new[] { IdentityPermissions.ManageShippingMethods, IdentityPermissions.ManageShipments, IdentityPermissions.ViewShipments })
                options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser().RequireClaim("permission", permission));
        });
        return services;
    }
}
