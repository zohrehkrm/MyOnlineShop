using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Cart.Infrastructure;

namespace MyOnlineShop.Cart.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddCartModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCartInfrastructure(configuration);
        services.AddControllers().AddApplicationPart(typeof(CartController).Assembly);
        return services;
    }
}
