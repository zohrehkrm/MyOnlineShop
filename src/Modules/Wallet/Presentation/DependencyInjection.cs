using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Wallet.Infrastructure;

namespace MyOnlineShop.Wallet.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddWalletModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddWalletInfrastructure(configuration);
        services.AddControllers().AddApplicationPart(typeof(WalletController).Assembly);
        services.AddAuthorization(options => options.AddPolicy(IdentityPermissions.CreditWallet,
            policy => policy.RequireAuthenticatedUser().RequireClaim("permission", IdentityPermissions.CreditWallet)));
        return services;
    }
}
