using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Infrastructure;

namespace MyOnlineShop.Identity.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddIdentityInfrastructure(configuration);
        services.AddControllers().AddApplicationPart(typeof(IdentityController).Assembly);
        services.AddAuthorization(options =>
        {
            options.AddPolicy(IdentityPermissions.AdministratorRole, policy =>
                policy.RequireAuthenticatedUser().RequireRole(IdentityPermissions.AdministratorRole));
            foreach (var permission in new[] { IdentityPermissions.ManageUsers, IdentityPermissions.ManageRoles })
                options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(permission)));
        });
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        return services;
    }
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true && context.User.HasClaim("permission", requirement.Permission))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
