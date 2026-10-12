using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.Foundation;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Presentation;
using MyOnlineShop.Catalog.Presentation;
using MyOnlineShop.Inventory.Presentation;
using MyOnlineShop.Cart.Presentation;
using MyOnlineShop.Pricing.Presentation;
using MyOnlineShop.Discount.Presentation;
using MyOnlineShop.Order.Presentation;
using MyOnlineShop.Wallet.Presentation;
using MyOnlineShop.BuildingBlocks.Infrastructure.Messaging;
using MyOnlineShop.Refund.Infrastructure;
using MyOnlineShop.Shipping.Presentation;
using MyOnlineShop.Reporting.Presentation;
using MyOnlineShop.Payment.Presentation;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});
builder.Services.AddFoundationInfrastructure(builder.Configuration);
builder.Services.AddApiFoundation();
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddCatalogModule(builder.Configuration);
builder.Services.AddInventoryModule(builder.Configuration);
builder.Services.AddCartModule(builder.Configuration);
builder.Services.AddPricingModule(builder.Configuration);
builder.Services.AddDiscountModule(builder.Configuration);
builder.Services.AddOrderModule(builder.Configuration);
builder.Services.AddPaymentModule(builder.Configuration);
builder.Services.AddWalletModule(builder.Configuration);
builder.Services.AddRefundInfrastructure(builder.Configuration);
builder.Services.AddShippingModule(builder.Configuration);
builder.Services.AddReportingModule();
builder.Services.AddMessagingWorkers(builder.Configuration);
builder.Services.AddScoped<IRequestContext>(provider =>
{
    var message = provider.GetRequiredService<MessageRequestContext>();
    return string.IsNullOrEmpty(message.CorrelationId) ? new HttpRequestContext(provider.GetRequiredService<IHttpContextAccessor>()) : message;
});
builder.Services.AddApiDocumentation();
builder.Services.Configure<DatabaseInitializationOptions>(builder.Configuration.GetSection("DatabaseInitialization"));
builder.Services.AddScoped<IDatabaseMigrationRunner, EfDatabaseMigrationRunner>();
builder.Services.AddScoped<DatabaseInitializer>();

var app = builder.Build();
if (builder.Configuration.GetValue<bool>("BootstrapIdentityAdmin"))
{
    throw new InvalidOperationException("Legacy BootstrapIdentityAdmin mode is retired. Use IdentityBootstrap:Enabled and a securely configured IdentityBootstrap:Password.");
}
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>()
        .InitializeAsync(app.Lifetime.ApplicationStopping);
}
app.UseRequestLocalization();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "MyOnlineShop API v1"));
}
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/api/v1/health", (HttpContext context) =>
    Results.Ok(new ApiResponse<HealthResponse>(new("Healthy"), context.TraceIdentifier)))
    .WithName("GetHealth")
    .WithSummary("Reports API process liveness; does not check database connectivity.");
app.Run();

public partial class Program;
