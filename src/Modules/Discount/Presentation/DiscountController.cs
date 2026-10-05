using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Discount.Application;
using MyOnlineShop.Discount.Contracts;
using MyOnlineShop.Identity.Contracts;

namespace MyOnlineShop.Discount.Presentation;

[ApiController, Authorize(Policy = IdentityPermissions.ManageDiscount), Route("api/v1/discounts")]
public sealed class DiscountController(IDiscountCommands commands, IDiscountQueries queries) : ControllerBase
{
    private Guid Actor => Guid.TryParse(User.FindFirst("sub")?.Value, out var id) && id != Guid.Empty ? id :
        throw new DiscountException("discount_user", 401, "A valid authenticated user is required.");
    private ApiResponse<T> Envelope<T>(T value) => new(value, HttpContext.TraceIdentifier);
    [HttpPost]
    public async Task<IActionResult> Create(DiscountInput input, CancellationToken ct)
    {
        var discount = await commands.CreateAsync(input, Actor, ct);
        return CreatedAtAction(nameof(Get), new { id = discount.Id }, Envelope(discount));
    }
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, DiscountInput input, CancellationToken ct) => Ok(Envelope(await commands.UpdateAsync(id, input, Actor, ct)));
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> Status(Guid id, DiscountStatusInput input, CancellationToken ct) =>
        Ok(Envelope(await commands.SetActiveAsync(id, input.IsActive!.Value, Actor, ct)));
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetAsync(id, ct)));
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct, int page = 1, int pageSize = 20) => Ok(Envelope(await queries.ListAsync(page, pageSize, ct)));
}
