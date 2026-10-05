using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Cart.Application;
using MyOnlineShop.Cart.Contracts;

namespace MyOnlineShop.Cart.Presentation;

[ApiController, Authorize, Route("api/v1/cart")]
public sealed class CartController(ICartCommands commands, ICartQueries queries) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirst("sub")?.Value, out var id) && id != Guid.Empty ? id :
        throw new CartException("cart_user", 401, "A valid authenticated user is required.");
    private ApiResponse<T> Envelope<T>(T data) => new(data, HttpContext.TraceIdentifier);
    [HttpGet]
    public async Task<ActionResult<ApiResponse<CartDto>>> Get(CancellationToken ct) => Ok(Envelope(await queries.GetCurrentAsync(UserId, ct)));
    [HttpPost("items")]
    public async Task<ActionResult<ApiResponse<CartDto>>> Add(AddCartItem command, CancellationToken ct) =>
        Ok(Envelope(await commands.AddAsync(UserId, command, ct)));
    [HttpPut("items/{id:guid}")]
    public async Task<ActionResult<ApiResponse<CartDto>>> Update(Guid id, UpdateCartItemQuantity command, CancellationToken ct) =>
        Ok(Envelope(await commands.UpdateQuantityAsync(UserId, id, command, ct)));
    [HttpDelete("items/{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    { await commands.RemoveAsync(UserId, id, ct); return NoContent(); }
    [HttpDelete]
    public async Task<IActionResult> Clear(CancellationToken ct)
    { await commands.ClearAsync(UserId, ct); return NoContent(); }
}
