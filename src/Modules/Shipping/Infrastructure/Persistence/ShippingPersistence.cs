using MyOnlineShop.BuildingBlocks.Infrastructure.Caching;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Pricing.Contracts;
using MyOnlineShop.Shipping.Application;
using MyOnlineShop.Shipping.Contracts;
using MyOnlineShop.Shipping.Domain;

namespace MyOnlineShop.Shipping.Infrastructure.Persistence;

public sealed class ShippingStore(ShippingDbContext db) : IShippingStore
{
    public Task<ShippingMethod?> MethodAsync(Guid id, CancellationToken ct) => db.Methods.SingleOrDefaultAsync(value => value.Id == id, ct);
    public Task<bool> CodeExistsAsync(string code, Guid? except, CancellationToken ct) => db.Methods.AnyAsync(value => value.Code == code && value.Id != except, ct);
    public Task<Shipment?> ShipmentAsync(Guid id, CancellationToken ct) => db.Shipments.SingleOrDefaultAsync(value => value.Id == id, ct);
    public Task<Shipment?> OrderShipmentAsync(Guid order, CancellationToken ct) => db.Shipments.SingleOrDefaultAsync(value => value.OrderId == order, ct);
    public void Add(ShippingMethod method) => db.Methods.Add(method);
    public void Add(Shipment shipment) => db.Shipments.Add(shipment);
    public void Audit(ShippingAudit audit) => db.Audit.Add(audit);
}
public sealed class ShippingUnitOfWork(ShippingDbContext db, ReadCache? cache = null) : IShippingUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        try
        {
            var committed = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var result = await action(ct); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return result;
            });
            if (cache is not null && committed is ShippingMethodDto) await cache.InvalidateAsync("shipping");
            return committed;
        }
        catch (ShippingRuleException error) { throw ShippingException.Invalid(error.Message); }
        catch (MoneyRuleException error) { throw ShippingException.Invalid(error.Message); }
        catch (DbUpdateConcurrencyException) { throw ShippingException.Conflict(); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 }) { throw ShippingException.Conflict(); }
    }
}
public sealed class ShippingQueries(ShippingDbContext db, IOrderShippingSnapshots orders, ReadCache? cache = null) : IShippingQueries
{
    public Task<IReadOnlyList<ShippingMethodDto>> AvailableMethodsAsync(string currency, CancellationToken ct)
    {
        try { currency = MoneyRules.Currency(currency); } catch (MoneyRuleException error) { throw ShippingException.Invalid(error.Message); }
        return cache is null ? AvailableAsync(currency, ct) : cache.GetAsync("shipping", $"methods:{currency}:active", token => AvailableAsync(currency, token), ct);
    }
    private async Task<IReadOnlyList<ShippingMethodDto>> AvailableAsync(string currency, CancellationToken ct)
    {
        try { currency = MoneyRules.Currency(currency); } catch (MoneyRuleException error) { throw ShippingException.Invalid(error.Message); }
        return await db.Methods.AsNoTracking().Where(value => value.IsActive && value.Currency == currency).OrderBy(value => value.Code)
            .Select(value => new ShippingMethodDto(value.Id, value.Name, value.Code, value.Description, value.BaseCost, value.Currency, value.IsActive, value.RequiresTracking, value.Revision)).ToListAsync(ct);
    }
    public async Task<IReadOnlyList<ShippingMethodDto>> MethodsAsync(CancellationToken ct) => await db.Methods.AsNoTracking().OrderBy(value => value.Code)
        .Select(value => new ShippingMethodDto(value.Id, value.Name, value.Code, value.Description, value.BaseCost, value.Currency, value.IsActive, value.RequiresTracking, value.Revision)).ToListAsync(ct);
    public async Task<ShipmentDto> GetMyOrderAsync(Guid user, Guid orderId, CancellationToken ct)
    {
        ShippingException.User(user); var order = await orders.GetAsync(orderId, ct);
        if (order?.UserId != user) throw ShippingException.NotFound();
        return await Detail(db.Shipments.Where(value => value.OrderId == orderId && value.UserId == user)).SingleOrDefaultAsync(ct) ?? throw ShippingException.NotFound();
    }
    public async Task<ShipmentDto> GetAsync(Guid id, CancellationToken ct) =>
        await Detail(db.Shipments.Where(value => value.Id == id)).SingleOrDefaultAsync(ct) ?? throw ShippingException.NotFound();
    public async Task<ShipmentPage> ListAsync(ShipmentListQuery query, CancellationToken ct)
    {
        if (query.Page < 1 || query.PageSize is < 1 or > 100 || ((long)query.Page - 1) * query.PageSize > int.MaxValue ||
            query.OrderId == Guid.Empty || query.ShippingMethodId == Guid.Empty ||
            (query.FromUtc is { } from && query.ToUtc is { } to && from >= to)) throw ShippingException.Invalid();
        var values = db.Shipments.AsNoTracking();
        if (query.Status is not null)
        {
            if (!Enum.TryParse<ShipmentStatus>(query.Status, false, out var status) || !Enum.IsDefined(status) || status.ToString() != query.Status) throw ShippingException.Invalid();
            values = values.Where(value => value.Status == status);
        }
        if (query.OrderId is not null) values = values.Where(value => value.OrderId == query.OrderId);
        if (query.ShippingMethodId is not null) values = values.Where(value => value.ShippingMethodId == query.ShippingMethodId);
        if (query.FromUtc is not null) { var fromUtc = query.FromUtc.Value.ToUniversalTime(); values = values.Where(value => value.CreatedAtUtc >= fromUtc); }
        if (query.ToUtc is not null) { var toUtc = query.ToUtc.Value.ToUniversalTime(); values = values.Where(value => value.CreatedAtUtc < toUtc); }
        var total = await values.CountAsync(ct);
        var items = await values.OrderByDescending(value => value.CreatedAtUtc).ThenBy(value => value.Id).Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(value => new ShipmentSummaryDto(value.Id, value.OrderId, value.ShippingMethodId, value.MethodName, value.Status.ToString(),
                value.TrackingNumber, value.Carrier, value.CreatedAtUtc, value.Revision)).ToListAsync(ct);
        return new(items, query.Page, query.PageSize, total);
    }
    private static IQueryable<ShipmentDto> Detail(IQueryable<Shipment> query) => query.AsNoTracking().Select(value => new ShipmentDto(value.Id, value.OrderId,
        value.ShippingMethodId, value.MethodName, value.MethodCode, value.Address, value.ShippingCost, value.Currency, value.Status.ToString(),
        value.TrackingNumber, value.Carrier, value.CreatedAtUtc, value.ShippedAtUtc, value.DeliveredAtUtc, value.Revision));
}
