using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions.Messaging;
using MyOnlineShop.BuildingBlocks.Infrastructure.Persistence;
using MyOnlineShop.Inventory.Contracts;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Payment.Application;
using MyOnlineShop.Payment.Contracts;
using MyOnlineShop.Payment.Infrastructure.Persistence;
using MyOnlineShop.Refund.Contracts;
namespace MyOnlineShop.Payment.Infrastructure;
public static class DependencyInjection {
 public static IServiceCollection AddPaymentInfrastructure(this IServiceCollection services,IConfiguration configuration) {
  services.AddOptions<PaymentGatewayOptions>().Bind(configuration.GetSection("PaymentGateway"))
   .Validate(x => x.IsValid(),"Payment gateway configuration is incomplete or insecure.").ValidateOnStart();
  services.AddOptions<PaymentOptions>().Bind(configuration.GetSection("Payment"))
   .Validate(x => x.ProviderTimeoutSeconds is >= 1 and <= 120 && x.FulfillmentWarehouseId != Guid.Empty,"Payment configuration is invalid.").ValidateOnStart();
  services.AddDbContext<PaymentDbContext>((provider,options) => {
   var settings = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
   options.UseSqlServer(configuration.GetConnectionString("SqlServer"),sql => {
    sql.CommandTimeout(settings.CommandTimeoutSeconds); sql.EnableRetryOnFailure(settings.MaxRetryCount);
    sql.MigrationsAssembly(typeof(PaymentDbContext).Assembly.FullName); sql.MigrationsHistoryTable("__EFMigrationsHistory","payment");
   });
  });
  services.TryAddScoped<IPaymentProvider,UnconfiguredPaymentProvider>();
  services.AddScoped<IPaymentAllocation,ConfiguredWarehouseAllocation>(); services.AddScoped<IPaymentStore,PaymentStore>();
  services.AddScoped<IPaymentQueries,PaymentStore>(); services.AddScoped<IPaymentUnitOfWork,PaymentUnitOfWork>(); services.AddScoped<IPaymentCommands,PaymentCommands>();
  services.AddScoped<PaymentOrchestration>();
  services.AddScoped<IInventoryPaymentOrchestration>(p => p.GetRequiredService<PaymentOrchestration>());
  services.AddScoped<IRefundPaymentPreparation>(p => p.GetRequiredService<PaymentOrchestration>());
  services.AddScoped<IRefundPaymentEvidence>(p => p.GetRequiredService<PaymentOrchestration>());
  services.AddScoped<IMessageConsumer,PaymentRefundCompletedConsumer>();
  services.AddScoped<ILocalSqlTransactionParticipant>(p => new LocalSqlTransactionParticipant<PaymentDbContext>(p.GetRequiredService<PaymentDbContext>(),"payment"));
  return services;
 }
}

