using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Identity.Contracts;

namespace MyOnlineShop.Catalog.Presentation;

[ApiController, Route("api/v1/catalog")]
public sealed class CatalogProductsController(ICatalogCommands commands, ICatalogQueries queries) : ControllerBase
{
    private ApiResponse<T> Envelope<T>(T data) => new(data, HttpContext.TraceIdentifier);

    [AllowAnonymous, HttpGet("products")]
    public async Task<ActionResult<ApiResponse<PageDto<ProductSummaryDto>>>> List([FromQuery] CatalogListQuery query, CancellationToken ct) =>
        Ok(Envelope(await queries.ListProductsAsync(query, false, ct)));
    [AllowAnonymous, HttpGet("products/{id:guid}")]
    public async Task<ActionResult<ApiResponse<ProductDto>>> Get(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetProductAsync(id, false, ct)));

    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpGet("manage/products")]
    public async Task<ActionResult<ApiResponse<PageDto<ProductSummaryDto>>>> ManageList([FromQuery] CatalogListQuery query, CancellationToken ct) =>
        Ok(Envelope(await queries.ListProductsAsync(query, true, ct)));
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpGet("manage/products/{id:guid}")]
    public async Task<ActionResult<ApiResponse<ProductDto>>> ManageGet(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetProductAsync(id, true, ct)));

    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpPost("products")]
    public async Task<ActionResult<ApiResponse<ProductDto>>> Create(ProductInput input, CancellationToken ct)
    {
        var dto = await commands.CreateProductAsync(input, ct);
        return CreatedAtAction(nameof(ManageGet), new { id = dto.Id }, Envelope(dto));
    }
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpPut("products/{id:guid}")]
    public async Task<ActionResult<ApiResponse<ProductDto>>> Update(Guid id, ProductInput input, CancellationToken ct) =>
        Ok(Envelope(await commands.UpdateProductAsync(id, input, ct)));

    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpPost("products/{productId:guid}/variants")]
    public async Task<ActionResult<ApiResponse<VariantDto>>> AddVariant(Guid productId, VariantInput input, CancellationToken ct)
    {
        var variant = await commands.AddVariantAsync(productId, input, ct);
        return CreatedAtAction(nameof(ManageGet), new { id = productId }, Envelope(variant));
    }
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpPut("products/{productId:guid}/variants/{variantId:guid}")]
    public async Task<ActionResult<ApiResponse<VariantDto>>> UpdateVariant(Guid productId, Guid variantId, VariantInput input, CancellationToken ct) =>
        Ok(Envelope(await commands.UpdateVariantAsync(productId, variantId, input, ct)));
}
