using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Order.Domain;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Shipping.Application;
using MyOnlineShop.Shipping.Contracts;
using MyOnlineShop.Shipping.Domain;
using MyOnlineShop.Shipping.Infrastructure;
using MyOnlineShop.Shipping.Infrastructure.Persistence;

namespace MyOnlineShop.Shipping.Tests;

// Explicit test unit only; EF InMemory does not prove SQL transaction/concurrency behavior.
internal sealed class MemoryUnit(ShippingDbContext db) : IShippingUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        try { var result = await action(ct); await db.SaveChangesAsync(ct); return result; }
        catch (ShippingRuleException error) { db.ChangeTracker.Clear(); throw ShippingException.Invalid(error.Message); }
        catch (MoneyRuleException error) { db.ChangeTracker.Clear(); throw ShippingException.Invalid(error.Message); }
        catch { db.ChangeTracker.Clear(); throw; }
    }
}
internal static class MemoryShipping
{
    public static void Configure(IServiceCollection services)
    {
        services.RemoveAll<ShippingDbContext>(); services.RemoveAll<DbContextOptions<ShippingDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<ShippingDbContext>>();
        var name = Guid.NewGuid().ToString(); services.AddDbContext<ShippingDbContext>(options => options.UseInMemoryDatabase(name));
        services.RemoveAll<IShippingUnitOfWork>(); services.AddScoped<IShippingUnitOfWork, MemoryUnit>();
    }
}
internal sealed class Harness : IDisposable
{
    public MyOnlineShop.Order.Tests.Harness Order { get; } = new(services =>
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:SqlServer"] = "Server=localhost;Database=ShippingOffline;Integrated Security=True" }).Build();
        services.AddShippingInfrastructure(config); MemoryShipping.Configure(services);
    });
    public Guid Actor { get; } = Guid.NewGuid();
    public IShippingCommands Commands => Order.Services.GetRequiredService<IShippingCommands>();
    public IShippingQueries Queries => Order.Services.GetRequiredService<IShippingQueries>();
    public IShippingQuotes Quotes => Order.Services.GetRequiredService<IShippingQuotes>();
    public ShippingDbContext Db => Order.Services.GetRequiredService<ShippingDbContext>();
    public static ShippingAddressInput Address(string recipient = "Original Recipient") => new()
    { Recipient = recipient, PhoneNumber = "+989123456789", State = "Tehran", City = "Tehran", Street = "Original street", PostalCode = "1234567890", CountryCode = "IR", Building = "Building A", Unit = "2" };
    public static ShippingMethodInput Method(string code = "STANDARD", decimal cost = 50_000m, string currency = "IRR", bool active = true, bool tracking = true) =>
        new() { Code = code, Name = code, BaseCost = cost, Currency = currency, IsActive = active, RequiresTracking = tracking };
    public Task<ShippingMethodDto> CreateMethod(ShippingMethodInput? input = null) => Commands.CreateMethodAsync(Actor, input ?? Method(), default);
    public async Task<OrderDto> Checkout(ShippingMethodDto method, ShippingAddressInput? address = null, Guid? key = null)
    {
        await Order.Seed(); await Order.Add();
        return await Order.Checkout(new CheckoutInput { IdempotencyKey = key ?? Guid.NewGuid(), Currency = method.Currency,
            Shipping = new() { ShippingMethodId = method.Id, Address = address ?? Address() } });
    }
    public async Task Paid(Guid id)
    {
        // Explicit trusted Order domain fixture state, not Payment verification or a fake Payment module.
        var db = Order.Services.GetRequiredService<OrderDbContext>(); db.ChangeTracker.Clear();
        var order = await db.Orders.SingleAsync(value => value.Id == id);
        order.Transition(OrderStatus.Paid, MyOnlineShop.Order.Tests.FixedClock.Now); order.Transition(OrderStatus.Processing, MyOnlineShop.Order.Tests.FixedClock.Now);
        await db.SaveChangesAsync();
    }
    public async Task<ShipmentDto> Shipment(ShippingMethodDto? method = null)
    { method ??= await CreateMethod(); var order = await Checkout(method); await Paid(order.Id); return await Commands.CreateShipmentAsync(Actor, order.Id, default); }
    public Task<ShipmentDto> Status(ShipmentDto shipment, string next) => Commands.ChangeStatusAsync(Actor, shipment.Id,
        new() { ExpectedRevision = shipment.Revision, Status = next }, default);
    public void Dispose() => Order.Dispose();
}
