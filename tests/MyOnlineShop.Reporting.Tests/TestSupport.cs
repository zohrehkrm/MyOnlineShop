using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Catalog.Domain;
using MyOnlineShop.Catalog.Infrastructure.Persistence;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Identity.Domain;
using MyOnlineShop.Identity.Infrastructure.Persistence;
using MyOnlineShop.Inventory.Domain;
using MyOnlineShop.Inventory.Infrastructure.Persistence;
using MyOnlineShop.Order.Domain;
using MyOnlineShop.Order.Infrastructure.Persistence;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Reporting.Application;
using MyOnlineShop.Reporting.Contracts;
using MyOnlineShop.Shipping.Contracts;
using MyOnlineShop.Shipping.Domain;
using MyOnlineShop.Shipping.Infrastructure.Persistence;
using MyOnlineShop.Wallet.Contracts;
using MyOnlineShop.Wallet.Domain;
using MyOnlineShop.Wallet.Infrastructure.Persistence;
using OrderEntity = MyOnlineShop.Order.Domain.Order;

namespace MyOnlineShop.Reporting.Tests;

internal sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
}
internal sealed class Harness : IDisposable
{
    public static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset To = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    public static ReportWindow Window(string? currency = null, string? status = null) => new(From, To, currency, status);
    public static ReportRequest Request(int page = 1, int size = 20, string? currency = null, string? status = null) =>
        new() { FromUtc = From, ToUtc = To, Page = page, PageSize = size, Currency = currency, OrderStatus = status };
    private static DbContextOptions<T> Options<T>() where T : DbContext => new DbContextOptionsBuilder<T>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    public OrderDbContext Orders { get; } = new(Options<OrderDbContext>());
    public InventoryDbContext Inventory { get; } = new(Options<InventoryDbContext>());
    public IdentityDbContext Identity { get; } = new(Options<IdentityDbContext>());
    public CatalogDbContext Catalog { get; } = new(Options<CatalogDbContext>());
    public WalletDbContext Wallet { get; } = new(Options<WalletDbContext>());
    public ShippingDbContext Shipping { get; } = new(Options<ShippingDbContext>());
    public Guid BuyerA { get; } = Guid.NewGuid();
    public Guid BuyerB { get; } = Guid.NewGuid();
    public Guid Variant { get; } = Guid.NewGuid();
    public Guid Warehouse { get; private set; }
    public ReportingQueries Queries => new(new OrderReporting(Orders), new InventoryReporting(Inventory), new CustomerReporting(Identity),
        new CatalogReporting(Catalog), new WalletReporting(Wallet), new ShippingReporting(Shipping), new FixedClock());
    public IEnumerable<DbContext> Contexts => [Orders, Inventory, Identity, Catalog, Wallet, Shipping];
    public async Task SeedAsync()
    {
        Orders.Orders.AddRange(
            Order(BuyerA, Variant, "IRR", 2, 100m, 10m, OrderStatus.Paid, From),
            Order(BuyerA, Variant, "IRR", 1, 150m, 0m, OrderStatus.Completed, From.AddDays(1)),
            Order(BuyerB, Guid.NewGuid(), "IRR", 1, 50m, 0m, OrderStatus.Pending, From.AddDays(2)),
            Order(BuyerB, Variant, "USD", 1, 4m, 0m, OrderStatus.Cancelled, From.AddDays(2)),
            Order(BuyerB, Variant, "USD", 3, 2m, 0m, OrderStatus.Processing, From.AddDays(2)),
            Order(BuyerA, Variant, "IRR", 1, 999m, 0m, OrderStatus.Paid, To),
            Order(BuyerA, Variant, "IRR", 1, 888m, 0m, OrderStatus.Paid, From.AddTicks(-1)));
        await Orders.SaveChangesAsync();
        var active = User.Create("active@example.com", "First", "Last", null, From); active.SetPasswordHash("never-report-this-hash");
        var inactive = User.Create("inactive@example.com", "First", "Last", null, From.AddDays(1)); inactive.SetPasswordHash("hash"); inactive.ChangeStatus(UserStatus.Inactive);
        var admin = User.Create("admin@example.com", "First", "Last", null, From); admin.SetPasswordHash("hash");
        Identity.Users.AddRange(active, inactive, admin);
        Identity.UserRoles.AddRange(new(active.Id, IdentityPermissions.CustomerRoleId), new(inactive.Id, IdentityPermissions.CustomerRoleId), new(admin.Id, IdentityPermissions.AdministratorRoleId));
        await Identity.SaveChangesAsync();
        var category = Category.Create("Category", "CATEGORY", null, true); Catalog.Categories.Add(category);
        var product = Product.Create("Name", "Description", category.Id, null, ProductKind.Physical, From);
        product.AddVariant("REPORT-SKU", true, null, null, [], From);
        product.Update("Name", "Description", category.Id, null, ProductKind.Physical, ProductStatus.Active, null, null, From);
        Catalog.Products.Add(product); await Catalog.SaveChangesAsync();
        var warehouse = MyOnlineShop.Inventory.Domain.Warehouse.Create("Main", "MAIN", true); Warehouse = warehouse.Id;
        var inactiveWarehouse = MyOnlineShop.Inventory.Domain.Warehouse.Create("Inactive", "INACTIVE", false);
        Inventory.Warehouses.AddRange(warehouse, inactiveWarehouse);
        var low = Stock.Create(warehouse.Id, Variant); low.Configure(true, 5);
        var other = Stock.Create(warehouse.Id, Guid.NewGuid()); other.Configure(true, 5);
        var ignored = Stock.Create(inactiveWarehouse.Id, Variant); ignored.Configure(true, 5);
        Inventory.Stocks.AddRange(low, other, ignored); await Inventory.SaveChangesAsync();
        Inventory.Entry(low).Property(x => x.Quantity).CurrentValue = 2;
        Inventory.Entry(other).Property(x => x.Quantity).CurrentValue = 7;
        Inventory.Movements.AddRange(Movement(low.Id, 10, 0, 10, MovementType.Receipt), Movement(low.Id, -8, 10, 2, MovementType.Sale), Movement(other.Id, 7, 0, 7, MovementType.ManualAdjustment));
        await Inventory.SaveChangesAsync();
        var account = WalletAccount.Create(BuyerA, "IRR", From); Wallet.Wallets.Add(account); await Wallet.SaveChangesAsync();
        await Post(account, 100m, WalletTransactionType.Credit, "AdminCredit"); await Post(account, 25m, WalletTransactionType.Debit, "Purchase");
        await Post(account, 10m, WalletTransactionType.Credit, "InventoryRefund");
        var usd = WalletAccount.Create(BuyerB, "USD", From); Wallet.Wallets.Add(usd); await Wallet.SaveChangesAsync(); await Post(usd, 3m, WalletTransactionType.Credit, "AdminCredit");
        var method = ShippingMethod.Create(new() { Name = "Standard", Code = "STANDARD", Currency = "IRR", BaseCost = 5m, IsActive = true });
        Shipping.Methods.Add(method); var quote = new ShippingQuoteSnapshot { ShippingMethodId = method.Id, MethodName = method.Name, MethodCode = method.Code,
            Currency = "IRR", Cost = 5m, Address = new("Name", "+989123456789", "State", "City", "Street", "12345", "IR", null, null) };
        var delivered = Shipment.Create(Guid.NewGuid(), BuyerA, quote, From);
        delivered.Transition(ShipmentStatus.Preparing, From); delivered.Transition(ShipmentStatus.Shipped, From.AddHours(1));
        delivered.Transition(ShipmentStatus.InTransit, From.AddHours(2)); delivered.Transition(ShipmentStatus.Delivered, From.AddHours(3));
        Shipping.Shipments.AddRange(delivered, Shipment.Create(Guid.NewGuid(), BuyerB, quote, From)); await Shipping.SaveChangesAsync();
        foreach (var db in Contexts) db.ChangeTracker.Clear();
    }
    private InventoryMovement Movement(Guid stock, long delta, long before, long after, MovementType type) =>
        InventoryMovement.Create(Guid.NewGuid(), stock, delta, before, after, type, "ref", "reason", BuyerA, new string('a', 64), "report-test", From);
    private async Task Post(WalletAccount account, decimal amount, WalletTransactionType type, string reference)
    {
        var before = account.Balance; var after = before + (type == WalletTransactionType.Credit ? amount : -amount);
        var change = new WalletBalanceChange(account.Id, before, after);
        Wallet.Entry(account).Property(x => x.Balance).CurrentValue = after; Wallet.RecordBalanceChange(change);
        Wallet.Ledger.Add(WalletLedger.Post(change, type, new() { IdempotencyKey = Guid.NewGuid(), Currency = account.Currency, Amount = amount,
            ReferenceType = reference, ReferenceId = Guid.NewGuid().ToString(), Description = "Fixture" }, new string('a', 64), BuyerA, From, "report-test"));
        await Wallet.SaveChangesAsync();
    }
    internal static OrderEntity Order(Guid user, Guid variant, string currency, int quantity, decimal unit, decimal discount, OrderStatus status, DateTimeOffset at)
    {
        var line = new PriceLineQuote(variant, quantity, Guid.NewGuid(), currency, unit, null, discount > 0 ? Guid.NewGuid() : null,
            discount, discount * quantity, unit - discount, (unit - discount) * quantity, []);
        var order = OrderEntity.Create(user, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), currency, at, at,
            [OrderItem.Snapshot(variant, "SKU", "Historical name", "Physical", line, currency)], null);
        if (status != OrderStatus.Pending)
        {
            order.Transition(OrderStatus.AwaitingPayment, at);
            if (status == OrderStatus.Cancelled) order.Transition(status, at);
            else
            {
                order.Transition(OrderStatus.Paid, at);
                if (status is OrderStatus.Processing or OrderStatus.Completed) order.Transition(OrderStatus.Processing, at);
                if (status == OrderStatus.Completed) { order.Transition(OrderStatus.Shipped, at); order.Transition(OrderStatus.Completed, at); }
            }
        }
        return order; // Trusted domain fixture only; no Payment module or verification is simulated.
    }
    public void Dispose() { foreach (var db in Contexts) db.Dispose(); }
}
