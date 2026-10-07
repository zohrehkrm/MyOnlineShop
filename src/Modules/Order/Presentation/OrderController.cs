using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Order.Application;
using MyOnlineShop.Order.Contracts;

namespace MyOnlineShop.Order.Presentation;

[ApiController, Authorize, Route("api/v1/orders")]
public sealed class OrderController(IOrderQueries queries, IOrderCommands commands) : ControllerBase
{
    private Guid Actor => Guid.TryParse(User.FindFirst("sub")?.Value, out var id) && id != Guid.Empty ? id :
        throw new OrderException("order_user", 401, "A valid authenticated user is required.");
    private ApiResponse<T> Envelope<T>(T value) => new(value, HttpContext.TraceIdentifier);
    [HttpGet]
    public async Task<IActionResult> MyOrders(CancellationToken ct, int page = 1, int pageSize = 20) =>
        Ok(Envelope(await queries.ListMyAsync(Actor, page, pageSize, ct)));
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> MyOrder(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetMyAsync(Actor, id, ct)));
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) => Ok(Envelope(await commands.CancelAsync(Actor, id, ct)));
    [HttpGet("management"), Authorize(Policy = IdentityPermissions.ViewOrders)]
    public async Task<IActionResult> All([FromQuery] OrderListQuery query, CancellationToken ct) =>
        Ok(Envelope(await queries.ListAsync(query, ct)));
    [HttpGet("management/{id:guid}"), Authorize(Policy = IdentityPermissions.ViewOrders)]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetAsync(id, ct)));
    [HttpPatch("management/{id:guid}/status"), Authorize(Policy = IdentityPermissions.ManageOrders)]
    public async Task<IActionResult> Status(Guid id, ChangeOrderStatusInput input, CancellationToken ct) =>
        Ok(Envelope(await commands.ChangeStatusAsync(Actor, id, input.Status, ct)));
}
[ApiController, Authorize, Route("api/v1/checkout")]
public sealed class CheckoutController(ICheckoutCommands commands) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CheckoutInput input, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("sub")?.Value, out var userId) || userId == Guid.Empty)
            throw new OrderException("order_user", 401, "A valid authenticated user is required.");
        var order = await commands.CreateAsync(userId, input, ct);
        return CreatedAtAction(nameof(OrderController.MyOrder), "Order", new { id = order.Id },
            new ApiResponse<OrderDto>(order, HttpContext.TraceIdentifier));
    }
}
