using Microsoft.Extensions.Logging;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Shipping.Contracts;
using MyOnlineShop.Shipping.Domain;

namespace MyOnlineShop.Shipping.Application;

public sealed class ShippingException(string code, int status, string message) : Exception(message), IApplicationError
{
    public string Code => code;
    public int StatusCode => status;
    public string SafeMessage => Message;
    public static ShippingException Invalid(string message = "Shipping input is invalid.") => new("shipping_validation", 400, message);
    public static ShippingException NotFound() => new("shipping_not_found", 404, "Shipping resource not found.");
    public static ShippingException Conflict() => new("shipping_conflict", 409, "Shipping state changed or resource already exists. Reload and retry.");
    public static void User(Guid actor) { if (actor == Guid.Empty) throw new ShippingException("shipping_user", 401, "A valid authenticated user is required."); }
}
public interface IShippingStore
{
    Task<ShippingMethod?> MethodAsync(Guid id, CancellationToken ct);
    Task<bool> CodeExistsAsync(string code, Guid? except, CancellationToken ct);
    Task<Shipment?> ShipmentAsync(Guid id, CancellationToken ct);
    Task<Shipment?> OrderShipmentAsync(Guid orderId, CancellationToken ct);
    void Add(ShippingMethod method);
    void Add(Shipment shipment);
    void Audit(ShippingAudit audit);
}
public interface IShippingUnitOfWork { Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct); }

public sealed class ShippingQuotes(IShippingStore store, TimeProvider clock) : IShippingQuotes
{
    public async Task<ShippingQuoteSnapshot> CalculateAsync(ShippingQuoteInput input, CancellationToken ct)
    {
        if (input.ShippingMethodId == Guid.Empty) throw ShippingException.Invalid();
        var method = await store.MethodAsync(input.ShippingMethodId, ct) ?? throw ShippingException.NotFound();
        try { return ShippingCost.Calculate(method, input.Address, input.Currency, clock.GetUtcNow()); }
        catch (ShippingRuleException error) { throw ShippingException.Invalid(error.Message); }
        catch (MyOnlineShop.Pricing.Contracts.MoneyRuleException error) { throw ShippingException.Invalid(error.Message); }
    }
}
public sealed class ShippingCommands(IShippingStore store, IShippingUnitOfWork unit, IOrderShippingSnapshots orders,
    TimeProvider clock, IRequestContext request, ILogger<ShippingCommands> logger) : IShippingCommands
{
    private void Audit(Guid id, Guid actor, string action)
    {
        store.Audit(ShippingAudit.Record(id, actor, action, clock.GetUtcNow(), request.CorrelationId));
        logger.LogInformation("Shipping operation {ShippingEntityId} {ActorId} {Action} {CorrelationId}", id, actor, action, request.CorrelationId);
    }
    private static void Revision(Guid expected, Guid actual) { if (expected == Guid.Empty || expected != actual) throw ShippingException.Conflict(); }
    public Task<ShippingMethodDto> CreateMethodAsync(Guid actor, ShippingMethodInput input, CancellationToken ct)
    {
        ShippingException.User(actor);
        return unit.ExecuteAsync(async token =>
        {
            var method = ShippingMethod.Create(input);
            if (await store.CodeExistsAsync(method.Code, null, token)) throw ShippingException.Conflict();
            store.Add(method); Audit(method.Id, actor, "MethodCreated"); return method.Dto();
        }, ct);
    }
    public Task<ShippingMethodDto> UpdateMethodAsync(Guid actor, Guid id, ShippingMethodUpdate input, CancellationToken ct)
    {
        ShippingException.User(actor);
        return unit.ExecuteAsync(async token =>
        {
            var method = await store.MethodAsync(id, token) ?? throw ShippingException.NotFound(); Revision(input.ExpectedRevision, method.Revision);
            method.Update(input.Method);
            if (await store.CodeExistsAsync(method.Code, id, token)) throw ShippingException.Conflict();
            Audit(method.Id, actor, "MethodUpdated"); return method.Dto();
        }, ct);
    }
    public Task<ShipmentDto> CreateShipmentAsync(Guid actor, Guid orderId, CancellationToken ct)
    {
        ShippingException.User(actor);
        return unit.ExecuteAsync(async token =>
        {
            var order = await orders.GetAsync(orderId, token) ?? throw ShippingException.NotFound();
            var existing = await store.OrderShipmentAsync(orderId, token);
            if (existing is not null)
            { if (existing.UserId != order.UserId) throw ShippingException.Conflict(); return existing.Dto(); }
            if (!order.HasPhysicalItems || order.Status is not ("Paid" or "Processing") || order.Shipping is null)
                throw ShippingException.Invalid("Shipment requires a paid physical order with a captured shipping selection.");
            var method = await store.MethodAsync(order.Shipping.ShippingMethodId, token) ?? throw ShippingException.NotFound();
            if (!method.IsActive || method.Currency != order.Shipping.Currency) throw ShippingException.Invalid("Shipping method is inactive or currency does not match.");
            var shipment = Shipment.Create(order.OrderId, order.UserId, order.Shipping, clock.GetUtcNow());
            store.Add(shipment); Audit(shipment.Id, actor, "ShipmentCreated"); return shipment.Dto();
        }, ct);
    }
    public Task<ShipmentDto> ChangeStatusAsync(Guid actor, Guid id, ShipmentStatusInput input, CancellationToken ct)
    {
        ShippingException.User(actor);
        return unit.ExecuteAsync(async token =>
        {
            var shipment = await store.ShipmentAsync(id, token) ?? throw ShippingException.NotFound(); Revision(input.ExpectedRevision, shipment.Revision);
            if (!Enum.TryParse<ShipmentStatus>(input.Status, false, out var status) || !Enum.IsDefined(status) || status.ToString() != input.Status)
                throw ShippingException.Invalid("Shipment status is invalid.");
            if (status == shipment.Status) return shipment.Dto();
            if (status != ShipmentStatus.Cancelled)
            {
                var order = await orders.GetAsync(shipment.OrderId, token) ?? throw ShippingException.NotFound();
                if (order.UserId != shipment.UserId || order.Status is not ("Paid" or "Processing" or "Shipped"))
                    throw ShippingException.Invalid("Order is not eligible for fulfillment.");
            }
            shipment.Transition(status, clock.GetUtcNow()); Audit(id, actor, "Status:" + status); return shipment.Dto();
        }, ct);
    }
    public Task<ShipmentDto> AssignTrackingAsync(Guid actor, Guid id, ShipmentTrackingInput input, CancellationToken ct)
    {
        ShippingException.User(actor);
        return unit.ExecuteAsync(async token =>
        {
            var shipment = await store.ShipmentAsync(id, token) ?? throw ShippingException.NotFound(); Revision(input.ExpectedRevision, shipment.Revision);
            shipment.Track(input.TrackingNumber, input.Carrier); Audit(id, actor, "TrackingAssigned"); return shipment.Dto();
        }, ct);
    }
}
