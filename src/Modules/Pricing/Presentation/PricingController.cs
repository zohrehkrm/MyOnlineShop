using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Identity.Contracts;
using MyOnlineShop.Pricing.Application;
using MyOnlineShop.Pricing.Contracts;

namespace MyOnlineShop.Pricing.Presentation;

[ApiController, Authorize, Route("api/v1/pricing")]
public sealed class PricingController(IPriceCommands commands, IPriceQueries queries, IPricingCalculation calculation, TimeProvider clock) : ControllerBase
{
    private Guid Actor => Guid.TryParse(User.FindFirst("sub")?.Value, out var id) && id != Guid.Empty ? id :
        throw new PricingException("pricing_user", 401, "A valid authenticated user is required.");
    private ApiResponse<T> Envelope<T>(T value) => new(value, HttpContext.TraceIdentifier);

    [HttpPost("prices"), Authorize(Policy = IdentityPermissions.ManagePricing)]
    public async Task<IActionResult> Create(PriceInput input, CancellationToken ct)
    {
        var price = await commands.CreateAsync(input, Actor, ct);
        return CreatedAtAction(nameof(GetRecord), new { id = price.Id }, Envelope(price));
    }
    [HttpGet("prices/{id:guid}"), Authorize(Policy = IdentityPermissions.ManagePricing)]
    public async Task<IActionResult> GetRecord(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetAsync(id, ct)));
    [HttpPut("prices/{id:guid}"), Authorize(Policy = IdentityPermissions.ManagePricing)]
    public async Task<IActionResult> Update(Guid id, PriceInput input, CancellationToken ct) => Ok(Envelope(await commands.UpdateAsync(id, input, Actor, ct)));
    [HttpPatch("prices/{id:guid}/status"), Authorize(Policy = IdentityPermissions.ManagePricing)]
    public async Task<IActionResult> Status(Guid id, PriceStatusInput input, CancellationToken ct) =>
        Ok(Envelope(await commands.SetActiveAsync(id, input.IsActive!.Value, Actor, ct)));
    [HttpGet("variants/{variantId:guid}")]
    public async Task<IActionResult> Current(Guid variantId, [FromQuery] string currency, CancellationToken ct) =>
        Ok(Envelope(await queries.GetCurrentAsync(variantId, currency, clock.GetUtcNow(), ct) ?? throw PricingException.NotFound()));
    [HttpGet("variants")]
    public async Task<IActionResult> CurrentMany([FromQuery] Guid[] variantIds, [FromQuery] string currency, CancellationToken ct) =>
        Ok(Envelope(await queries.GetCurrentManyAsync(variantIds, currency, clock.GetUtcNow(), ct)));
    [HttpGet("variants/{variantId:guid}/history"), Authorize(Policy = IdentityPermissions.ManagePricing)]
    public async Task<IActionResult> History(Guid variantId, [FromQuery] string currency, CancellationToken ct, int page = 1, int pageSize = 20) =>
        Ok(Envelope(await queries.GetHistoryAsync(variantId, currency, page, pageSize, ct)));
    [HttpPost("preview")]
    public async Task<IActionResult> Preview(PricingPreviewInput input, CancellationToken ct) =>
        Ok(Envelope(await calculation.CalculateAsync([new(input.ProductVariantId, input.Quantity)], input.Currency, input.CouponCode, ct)));
    [HttpGet("variants/{variantId:guid}/discounts")]
    public async Task<IActionResult> Applicable(Guid variantId, [FromQuery] string currency, CancellationToken ct, int quantity = 1, string? couponCode = null)
    {
        var quote = await calculation.CalculateAsync([new(variantId, quantity)], currency, couponCode, ct);
        return Ok(Envelope(quote.Lines[0].ApplicableDiscounts));
    }
}
