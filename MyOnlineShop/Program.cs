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
builder.Services.AddWalletModule(builder.Configuration);
builder.Services.AddOpenApi();

var app = builder.Build();
if (builder.Configuration.GetValue<bool>("BootstrapIdentityAdmin"))
{
    using var scope = app.Services.CreateScope();
    var command = builder.Configuration.GetSection("IdentityBootstrap")
        .Get<MyOnlineShop.Identity.Contracts.RegisterCommand>()
        ?? throw new InvalidOperationException("IdentityBootstrap must be supplied through secure configuration.");
    await scope.ServiceProvider.GetRequiredService<MyOnlineShop.Identity.Contracts.IIdentityAdministration>()
        .BootstrapAdministratorAsync(command, CancellationToken.None);
    return;
}
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
