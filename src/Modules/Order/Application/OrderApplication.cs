using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Order.Contracts;
using MyOnlineShop.Order.Domain;
using OrderAggregate = MyOnlineShop.Order.Domain.Order;

namespace MyOnlineShop.Order.Application;

public sealed class OrderException(string code, int status, string message) : Exception(message), IApplicationError
{
    public string Code => code;
    public int StatusCode => status;
    public string SafeMessage => Message;
    public static OrderException Invalid(string message = "Order or checkout input is invalid.") => new("order_validation", 400, message);
    public static OrderException NotFound() => new("order_not_found", 404, "Order not found.");
    public static OrderException Conflict(string message = "Order or Cart changed. Reload and retry.") => new("order_conflict", 409, message);
    public static void User(Guid id) { if (id == Guid.Empty) throw new OrderException("order_user", 401, "A valid authenticated user is required."); }
}
public interface IOrderStore
{
    Task<OrderAggregate?> GetAsync(Guid id, CancellationToken ct);
    Task<OrderAggregate?> GetCheckoutAsync(Guid userId, Guid key, CancellationToken ct);
    void Add(OrderAggregate order);
    void Audit(OrderAudit audit);
}
public interface IOrderUnitOfWork
{
    Task<T> ExecuteAsync<T>(bool includeCart, Func<CancellationToken, Task<T>> action, CancellationToken ct);
    Task LockCheckoutAsync(Guid userId, CancellationToken ct);
}
public interface IOrderReadStore : IOrderQueries;
public sealed class OrderCommands(IOrderStore store, IOrderUnitOfWork unit, TimeProvider clock, IRequestContext request) : IOrderCommands
{
    public Task<OrderDto> CancelAsync(Guid userId, Guid orderId, CancellationToken ct) => ChangeAsync(userId, orderId, OrderStatus.Cancelled, true, ct);
    public Task<OrderDto> ChangeStatusAsync(Guid actorId, Guid orderId, string status, CancellationToken ct)
    {
        // Payment success must never be asserted by a customer or an administrative status endpoint.
        if (!Enum.TryParse<OrderStatus>(status, false, out var next) || !Enum.IsDefined(next) || next.ToString() != status || next == OrderStatus.Paid)
            throw OrderException.Invalid("Status is invalid or requires the future verified-payment workflow.");
        return ChangeAsync(actorId, orderId, next, false, ct);
    }
    private async Task<OrderDto> ChangeAsync(Guid actor, Guid id, OrderStatus next, bool ownerOnly, CancellationToken ct)
    {
        OrderException.User(actor);
        return await unit.ExecuteAsync(false, async token =>
        {
            var order = await store.GetAsync(id, token) ?? throw OrderException.NotFound();
            if (ownerOnly && order.UserId != actor) throw OrderException.NotFound();
            order.Transition(next, clock.GetUtcNow());
            store.Audit(OrderAudit.Record(order.Id, actor, next.ToString(), clock.GetUtcNow(), request.CorrelationId));
            return order.Dto();
        }, ct);
    }
}
