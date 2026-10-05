using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.Foundation;

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
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "MyOnlineShop API v1"));
}
app.UseHttpsRedirection();
app.MapControllers();
app.MapGet("/api/v1/health", (HttpContext context) =>
    Results.Ok(new ApiResponse<HealthResponse>(new("Healthy"), context.TraceIdentifier)))
    .WithName("GetHealth")
    .WithSummary("Reports API process liveness; does not check database connectivity.");
app.Run();

public partial class Program;
