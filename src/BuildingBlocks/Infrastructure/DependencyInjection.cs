using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;

namespace MyOnlineShop.BuildingBlocks.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFoundationInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SqlServer");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:SqlServer must be configured.");

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .Validate(options => options.CommandTimeoutSeconds is > 0 and <= 300,
                "Database command timeout must be between 1 and 300 seconds.")
            .Validate(options => options.MaxRetryCount is >= 0 and <= 10,
                "Database retry count must be between 0 and 10.")
            .ValidateOnStart();

        services.AddDbContext<FoundationDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(connectionString, sql =>
            {
                sql.CommandTimeout(settings.CommandTimeoutSeconds);
                sql.EnableRetryOnFailure(settings.MaxRetryCount);
                sql.MigrationsAssembly(typeof(FoundationDbContext).Assembly.FullName);
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "foundation");
            });
        });
        return services;
    }
}
