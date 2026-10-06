using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Refund.Application;
using MyOnlineShop.Refund.Infrastructure.Persistence;

namespace MyOnlineShop.Refund.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRefundInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<RefundProcessingOptions>().Bind(configuration.GetSection("RefundProcessing"))
            .Validate(value => value.IsValid(), "Refund processing configuration is invalid.").ValidateOnStart();
        services.AddDbContext<RefundDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(configuration.GetConnectionString("SqlServer"), sql =>
            {
                sql.CommandTimeout(settings.CommandTimeoutSeconds); sql.EnableRetryOnFailure(settings.MaxRetryCount);
                sql.MigrationsAssembly(typeof(RefundDbContext).Assembly.FullName); sql.MigrationsHistoryTable("__EFMigrationsHistory", "refund");
            });
        });
        services.AddScoped<IRefundStore, RefundStore>(); services.AddScoped<IRefundUnitOfWork, RefundUnitOfWork>();
        services.AddScoped<RefundRequests>(); services.AddScoped<RefundScheduler>(); services.AddScoped<RefundCompletion>();
        services.AddScoped<IMessageConsumer, InventoryUnavailableConsumer>(); services.AddScoped<IMessageConsumer, RefundDueConsumer>();
        services.AddScoped<ILocalSqlTransactionParticipant>(provider => new LocalSqlTransactionParticipant<RefundDbContext>(provider.GetRequiredService<RefundDbContext>(), "refund"));
        // Phase 8 must register IRefundPaymentEvidence. Absence is an explicit production processing blocker.
        if (configuration.GetValue<bool>("RefundProcessing:Enabled")) services.AddHostedService<RefundWorker>();
        return services;
    }
}
