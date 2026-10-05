using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyOnlineShop.Cart.Application;
using MyOnlineShop.Cart.Domain;
using MyOnlineShop.Cart.Infrastructure.Persistence;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Discount.Application;
using MyOnlineShop.Discount.Domain;
using MyOnlineShop.Discount.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Application;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Pricing.Infrastructure.Persistence;

namespace MyOnlineShop.PricingDiscount.Tests;

internal sealed class MemoryDiscountUnit(DiscountDbContext db) : IDiscountUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        try { var result = await action(ct); await db.SaveChangesAsync(ct); return result; }
        catch (DiscountRuleException e) { throw DiscountException.Invalid(e.Message); }
        catch (MoneyRuleException e) { throw DiscountException.Invalid(e.Message); }
    }
}
internal sealed class MemoryCartUnit(CartDbContext db) : ICartUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        try { var result = await action(ct); await db.SaveChangesAsync(ct); return result; }
        catch (CartRuleException e) { throw CartException.Invalid(e.Message); }
    }
}
public sealed class PricingApiFactory : WebApplicationFactory<Program>
{
    internal CatalogReferences Catalog { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:SqlServer", "Server=localhost;Database=PricingOffline;Integrated Security=True");
        builder.UseSetting("IdentitySecurity:SigningKeyBase64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.ConfigureServices(services =>
        {
            ReplaceContext<PricingDbContext>(services); ReplaceContext<DiscountDbContext>(services); ReplaceContext<CartDbContext>(services);
            services.RemoveAll<IPriceUnitOfWork>(); services.AddScoped<IPriceUnitOfWork, MemoryPriceUnit>();
            services.RemoveAll<IDiscountUnitOfWork>(); services.AddScoped<IDiscountUnitOfWork, MemoryDiscountUnit>();
            services.RemoveAll<ICartUnitOfWork>(); services.AddScoped<ICartUnitOfWork, MemoryCartUnit>();
            services.RemoveAll<ICatalogVariantReferences>(); services.AddSingleton<ICatalogVariantReferences>(Catalog);
            services.RemoveAll<ICatalogQueries>(); services.AddSingleton<ICatalogQueries>(new CatalogTargetQueries(Catalog));
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider, FixedClock>();
            services.AddAuthentication(options =>
            { options.DefaultAuthenticateScheme = "PriceTest"; options.DefaultChallengeScheme = "PriceTest"; options.DefaultForbidScheme = "PriceTest"; })
                .AddScheme<AuthenticationSchemeOptions, PriceAuthentication>("PriceTest", _ => { });
        });
    }
    private static void ReplaceContext<T>(IServiceCollection services) where T : DbContext
    {
        services.RemoveAll<T>(); services.RemoveAll<DbContextOptions<T>>(); services.RemoveAll<IDbContextOptionsConfiguration<T>>();
        var name = Guid.NewGuid().ToString(); services.AddDbContext<T>(options => options.UseInMemoryDatabase(name));
    }
    public HttpClient Client(Guid? user = null, params string[] permissions)
    {
        var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        if (user is not null) client.DefaultRequestHeaders.Add("X-Test-User", user.ToString());
        if (permissions.Length > 0) client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(",", permissions));
        return client;
    }
}
internal sealed class PriceAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (user.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("sub", user) };
        claims.AddRange(Request.Headers["X-Test-Permissions"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("permission", p)));
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
