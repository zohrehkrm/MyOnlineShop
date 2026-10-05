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
using MyOnlineShop.Cart.Application;
using MyOnlineShop.Cart.Infrastructure;
using MyOnlineShop.Cart.Infrastructure.Persistence;
using MyOnlineShop.Cart.Domain;
using MyOnlineShop.Catalog.Contracts;

namespace MyOnlineShop.Cart.Tests;

internal sealed class VariantReferences : ICatalogVariantReferences
{
    public Guid Id { get; } = Guid.NewGuid();
    public bool Active { get; set; } = true;
    public Task<CatalogVariantReference?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(id == Id ? new CatalogVariantReference(Id, "CART-SKU", "Physical", Active) : null);
}
internal sealed class MemoryCartUnit(CartDbContext context) : ICartUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        context.ChangeTracker.Clear();
        try { var result = await action(ct); await context.SaveChangesAsync(ct); return result; }
        catch (CartRuleException error) { throw CartException.Invalid(error.Message); }
    }
}
internal static class MemoryCart
{
    public static void Configure(IServiceCollection services, VariantReferences variants)
    {
        services.RemoveAll<CartDbContext>(); services.RemoveAll<DbContextOptions<CartDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<CartDbContext>>();
        var name = "CartTests_" + Guid.NewGuid().ToString("N");
        services.AddDbContext<CartDbContext>(options => options.UseInMemoryDatabase(name));
        services.RemoveAll<ICartUnitOfWork>(); services.AddScoped<ICartUnitOfWork, MemoryCartUnit>();
        services.RemoveAll<ICatalogVariantReferences>(); services.AddSingleton<ICatalogVariantReferences>(variants);
    }
}
internal sealed class CartHarness : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    public IServiceProvider Services => _scope.ServiceProvider;
    public VariantReferences Catalog { get; } = new();
    public CartHarness()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:SqlServer"] = "Server=localhost;Database=CartOffline;Integrated Security=True" }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddFoundationInfrastructure(configuration); services.AddCartInfrastructure(configuration);
        MemoryCart.Configure(services, Catalog);
        _provider = services.BuildServiceProvider(); _scope = _provider.CreateScope();
    }
    public void Dispose() { _scope.Dispose(); _provider.Dispose(); }
}
public sealed class CartApiFactory : WebApplicationFactory<Program>
{
    internal VariantReferences Catalog { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:SqlServer", "Server=localhost;Database=CartOffline;Integrated Security=True");
        builder.UseSetting("IdentitySecurity:SigningKeyBase64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.ConfigureServices(services =>
        {
            MemoryCart.Configure(services, Catalog);
            services.AddAuthentication(options =>
            { options.DefaultAuthenticateScheme = "CartTest"; options.DefaultChallengeScheme = "CartTest"; options.DefaultForbidScheme = "CartTest"; })
                .AddScheme<AuthenticationSchemeOptions, CartAuthentication>("CartTest", _ => { });
        });
    }
    public HttpClient Client(Guid? userId = null)
    {
        var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        if (userId is not null) client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
        return client;
    }
}
internal sealed class CartAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (user.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new("sub", user)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
