using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Pricing.Infrastructure;
using MyOnlineShop.Identity.Contracts;

namespace MyOnlineShop.Pricing.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddPricingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPricingInfrastructure(configuration);
        services.AddControllers().AddApplicationPart(typeof(PricingController).Assembly);
        services.AddAuthorization(options => options.AddPolicy(IdentityPermissions.ManagePricing,
            policy => policy.RequireAuthenticatedUser().RequireClaim("permission", IdentityPermissions.ManagePricing)));
        return services;
    }
}
