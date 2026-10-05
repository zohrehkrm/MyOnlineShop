using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Infrastructure;
using MyOnlineShop.Catalog.Application;
using MyOnlineShop.Catalog.Domain;
using MyOnlineShop.Catalog.Infrastructure;
using MyOnlineShop.Catalog.Infrastructure.Persistence;
using MyOnlineShop.Identity.Contracts;

namespace MyOnlineShop.Catalog.Tests;

internal sealed class MemoryUnitOfWork(CatalogDbContext context) : ICatalogUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        context.ChangeTracker.Clear();
        try
        {
            var result = await action(ct);
            await context.SaveChangesAsync(ct);
            return result;
        }
        catch (CatalogRuleException error) { throw CatalogException.Invalid(error.Message); }
    }
    public Task LockCategoryHierarchyAsync(CancellationToken ct) => Task.CompletedTask;
}

internal static class MemoryCatalog
{
    public static void Configure(IServiceCollection services)
    {
        services.RemoveAll<CatalogDbContext>();
        services.RemoveAll<DbContextOptions<CatalogDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<CatalogDbContext>>();
        var name = "CatalogTests_" + Guid.NewGuid().ToString("N");
        services.AddDbContext<CatalogDbContext>(options => options.UseInMemoryDatabase(name));
        services.RemoveAll<ICatalogUnitOfWork>();
        services.AddScoped<ICatalogUnitOfWork, MemoryUnitOfWork>();
    }
}

public sealed class CatalogHarness : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    public IServiceProvider Services => _scope.ServiceProvider;
    public CatalogHarness()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:SqlServer"] = "Server=localhost;Database=CatalogOffline;Integrated Security=True" }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddFoundationInfrastructure(configuration);
        services.AddCatalogInfrastructure(configuration);
        MemoryCatalog.Configure(services);
        _provider = services.BuildServiceProvider(); _scope = _provider.CreateScope();
    }
    public void Dispose() { _scope.Dispose(); _provider.Dispose(); }
}

public sealed class CatalogApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:SqlServer", "Server=localhost;Database=CatalogOffline;Integrated Security=True");
        builder.UseSetting("IdentitySecurity:SigningKeyBase64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.ConfigureServices(services =>
        {
            MemoryCatalog.Configure(services);
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "CatalogTest";
                options.DefaultChallengeScheme = "CatalogTest";
                options.DefaultForbidScheme = "CatalogTest";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("CatalogTest", _ => { });
        });
    }
    public HttpClient Client(string? authorization = null)
    {
        var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        if (authorization is not null) client.DefaultRequestHeaders.Add("X-Test-Authorization", authorization);
        return client;
    }
}

internal sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers["X-Test-Authorization"].ToString();
        if (string.IsNullOrEmpty(authorization)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()) };
        if (authorization == "manage") claims.Add(new("permission", IdentityPermissions.ManageCatalog));
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
