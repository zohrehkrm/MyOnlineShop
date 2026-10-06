using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Shipping.Application;
using MyOnlineShop.Shipping.Contracts;

namespace MyOnlineShop.Shipping.Presentation;

[ApiController, Authorize, Route("api/v1/shipping")]
public sealed class ShippingController(IShippingCommands commands, IShippingQueries queries, IShippingQuotes quotes) : ControllerBase
{
    private Guid Actor => Guid.TryParse(User.FindFirst("sub")?.Value, out var id) && id != Guid.Empty ? id :
        throw new ShippingException("shipping_user", 401, "A valid authenticated user is required.");
    private ApiResponse<T> Envelope<T>(T value) => new(value, HttpContext.TraceIdentifier);
    [HttpGet("methods")]
    public async Task<IActionResult> Available(CancellationToken ct, string currency = "IRR")
    { _ = Actor; return Ok(Envelope(await queries.AvailableMethodsAsync(currency, ct))); }
    [HttpPost("quotes")]
    public async Task<IActionResult> Quote(ShippingQuoteInput input, CancellationToken ct)
    { _ = Actor; return Ok(Envelope(await quotes.CalculateAsync(input, ct))); }
    [HttpGet("orders/{orderId:guid}/shipment")]
    public async Task<IActionResult> MyShipment(Guid orderId, CancellationToken ct) => Ok(Envelope(await queries.GetMyOrderAsync(Actor, orderId, ct)));
    [HttpGet("management/methods"), Authorize(Policy = IdentityPermissions.ManageShippingMethods)]
    public async Task<IActionResult> Methods(CancellationToken ct) => Ok(Envelope(await queries.MethodsAsync(ct)));
    [HttpPost("management/methods"), Authorize(Policy = IdentityPermissions.ManageShippingMethods)]
    public async Task<IActionResult> CreateMethod(ShippingMethodInput input, CancellationToken ct)
    {
        var method = await commands.CreateMethodAsync(Actor, input, ct);
        return Created("/api/v1/shipping/management/methods", Envelope(method));
    }
    [HttpPut("management/methods/{id:guid}"), Authorize(Policy = IdentityPermissions.ManageShippingMethods)]
    public async Task<IActionResult> UpdateMethod(Guid id, ShippingMethodUpdate input, CancellationToken ct) =>
        Ok(Envelope(await commands.UpdateMethodAsync(Actor, id, input, ct)));
    [HttpPost("management/orders/{orderId:guid}/shipment"), Authorize(Policy = IdentityPermissions.ManageShipments)]
    public async Task<IActionResult> CreateShipment(Guid orderId, CancellationToken ct)
    {
        var shipment = await commands.CreateShipmentAsync(Actor, orderId, ct);
        return CreatedAtAction(nameof(Shipment), new { id = shipment.Id }, Envelope(shipment));
    }
    [HttpGet("management/shipments/{id:guid}"), Authorize(Policy = IdentityPermissions.ViewShipments)]
    public async Task<IActionResult> Shipment(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetAsync(id, ct)));
    [HttpPatch("management/shipments/{id:guid}/status"), Authorize(Policy = IdentityPermissions.ManageShipments)]
    public async Task<IActionResult> Status(Guid id, ShipmentStatusInput input, CancellationToken ct) =>
        Ok(Envelope(await commands.ChangeStatusAsync(Actor, id, input, ct)));
    [HttpPut("management/shipments/{id:guid}/tracking"), Authorize(Policy = IdentityPermissions.ManageShipments)]
    public async Task<IActionResult> Tracking(Guid id, ShipmentTrackingInput input, CancellationToken ct) =>
        Ok(Envelope(await commands.AssignTrackingAsync(Actor, id, input, ct)));
}
