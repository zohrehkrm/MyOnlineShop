using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.Payment.Infrastructure;
namespace MyOnlineShop.Payment.Presentation;
public static class DependencyInjection {
 public static IServiceCollection AddPaymentModule(this IServiceCollection services,IConfiguration configuration) {
  services.AddPaymentInfrastructure(configuration); services.AddControllers().AddApplicationPart(typeof(PaymentController).Assembly); return services;
 }
}

