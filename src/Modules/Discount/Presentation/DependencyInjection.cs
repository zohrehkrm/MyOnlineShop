using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Discount.Infrastructure;
using MyOnlineShop.Identity.Contracts;

namespace MyOnlineShop.Discount.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddDiscountModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDiscountInfrastructure(configuration);
        services.AddControllers().AddApplicationPart(typeof(DiscountController).Assembly);
        services.AddAuthorization(options => options.AddPolicy(IdentityPermissions.ManageDiscount,
            policy => policy.RequireAuthenticatedUser().RequireClaim("permission", IdentityPermissions.ManageDiscount)));
        return services;
    }
}
