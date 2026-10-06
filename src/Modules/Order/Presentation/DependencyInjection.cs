using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Order.Infrastructure;

namespace MyOnlineShop.Order.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOrderInfrastructure(configuration);
        services.AddControllers().AddApplicationPart(typeof(OrderController).Assembly);
        services.AddAuthorization(options =>
        {
            foreach (var permission in new[] { IdentityPermissions.ViewOrders, IdentityPermissions.ManageOrders })
                options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser().RequireClaim("permission", permission));
        });
        return services;
    }
}
