using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;

namespace MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;

public static class MessagingRegistration
{
    public static IServiceCollection AddMessagingPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<MessageRequestContext>();
        services.AddOptions<MessagingOptions>().Bind(configuration.GetSection("Messaging")).Validate(value => value.IsValid(), "Messaging configuration is invalid.").ValidateOnStart();
        services.AddDbContext<MessagingDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(configuration.GetConnectionString("SqlServer"), sql =>
            { sql.CommandTimeout(settings.CommandTimeoutSeconds); sql.EnableRetryOnFailure(settings.MaxRetryCount);
              sql.MigrationsAssembly(typeof(MessagingDbContext).Assembly.FullName); sql.MigrationsHistoryTable("__EFMigrationsHistory", "messaging"); });
        });
        services.AddScoped<IOutboxWriter, SqlOutboxWriter>(); services.AddScoped<IOutboxDeliveryStore, SqlOutboxDeliveryStore>();
        services.AddScoped<IMessageProcessor, SqlMessageProcessor>(); services.AddScoped<OutboxDispatcher>();
        services.AddScoped<ILocalSqlTransactionParticipant>(provider => new LocalSqlTransactionParticipant<MessagingDbContext>(provider.GetRequiredService<MessagingDbContext>(), "messaging"));
        return services;
    }
    public static IServiceCollection AddMessagingWorkers(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IRabbitMqConnectionFactory, RabbitMqConnectionFactory>(); services.AddSingleton<IMessagePublisher, RabbitMqPublisher>();
        if (configuration.GetValue<bool>("Messaging:Enabled"))
        { services.AddHostedService<OutboxWorker>(); services.AddHostedService<RabbitMqConsumerWorker>(); }
        return services;
    }
}
