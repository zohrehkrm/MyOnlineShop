using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Cart.Contracts;
using MyOnlineShop.Pricing.Application;
using MyOnlineShop.Pricing.Contracts;

namespace MyOnlineShop.Pricing.Presentation;

public sealed record CartPricingPreview(Guid? CartId, PricingQuote Quote);

[ApiController, Authorize, Route("api/v1/cart/pricing")]
public sealed class CartPricingController(ICartQueries carts, IPricingCalculation calculation) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string currency, CancellationToken ct, string? couponCode = null)
    {
        if (!Guid.TryParse(User.FindFirst("sub")?.Value, out var userId) || userId == Guid.Empty)
            throw new PricingException("pricing_user", 401, "A valid authenticated user is required.");
        var cart = await carts.GetCurrentAsync(userId, ct);
        var quote = await calculation.CalculateAsync(cart.Items.Select(item => new PriceLineRequest(item.ProductVariantId, item.Quantity)).ToArray(),
            currency, couponCode, ct);
        return Ok(new ApiResponse<CartPricingPreview>(new(cart.Id, quote), HttpContext.TraceIdentifier));
    }
}
