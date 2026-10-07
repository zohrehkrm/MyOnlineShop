using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Identity.Contracts;

namespace MyOnlineShop.Catalog.Presentation;

[ApiController, Route("api/v1/catalog")]
[ServiceFilter(typeof(CatalogAdministrationAudit))]
public sealed class CatalogTaxonomyController(ICatalogCommands commands, ICatalogQueries queries) : ControllerBase
{
    private ApiResponse<T> Envelope<T>(T data) => new(data, HttpContext.TraceIdentifier);
    [AllowAnonymous, HttpGet("categories")]
    public async Task<ActionResult<ApiResponse<PageDto<CategoryDto>>>> Categories([FromQuery] CatalogListQuery query, CancellationToken ct) => Ok(Envelope(await queries.ListCategoriesAsync(query, false, ct)));
    [AllowAnonymous, HttpGet("categories/{id:guid}")]
    public async Task<ActionResult<ApiResponse<CategoryDto>>> Category(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetCategoryAsync(id, false, ct)));
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpGet("manage/categories")]
    public async Task<ActionResult<ApiResponse<PageDto<CategoryDto>>>> ManageCategories([FromQuery] CatalogListQuery query, CancellationToken ct) => Ok(Envelope(await queries.ListCategoriesAsync(query, true, ct)));
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpGet("manage/categories/{id:guid}")]
    public async Task<ActionResult<ApiResponse<CategoryDto>>> ManageCategory(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetCategoryAsync(id, true, ct)));
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpPost("categories")]
    public async Task<ActionResult<ApiResponse<CategoryDto>>> CreateCategory(CategoryInput input, CancellationToken ct)
    {
        var dto = await commands.CreateCategoryAsync(input, ct); return CreatedAtAction(nameof(ManageCategory), new { id = dto.Id }, Envelope(dto));
    }
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpPut("categories/{id:guid}")]
    public async Task<ActionResult<ApiResponse<CategoryDto>>> UpdateCategory(Guid id, CategoryInput input, CancellationToken ct) => Ok(Envelope(await commands.UpdateCategoryAsync(id, input, ct)));

    [AllowAnonymous, HttpGet("brands")]
    public async Task<ActionResult<ApiResponse<PageDto<BrandDto>>>> Brands([FromQuery] CatalogListQuery query, CancellationToken ct) => Ok(Envelope(await queries.ListBrandsAsync(query, false, ct)));
    [AllowAnonymous, HttpGet("brands/{id:guid}")]
    public async Task<ActionResult<ApiResponse<BrandDto>>> Brand(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetBrandAsync(id, false, ct)));
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpGet("manage/brands")]
    public async Task<ActionResult<ApiResponse<PageDto<BrandDto>>>> ManageBrands([FromQuery] CatalogListQuery query, CancellationToken ct) => Ok(Envelope(await queries.ListBrandsAsync(query, true, ct)));
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpGet("manage/brands/{id:guid}")]
    public async Task<ActionResult<ApiResponse<BrandDto>>> ManageBrand(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetBrandAsync(id, true, ct)));
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpPost("brands")]
    public async Task<ActionResult<ApiResponse<BrandDto>>> CreateBrand(BrandInput input, CancellationToken ct)
    {
        var dto = await commands.CreateBrandAsync(input, ct); return CreatedAtAction(nameof(ManageBrand), new { id = dto.Id }, Envelope(dto));
    }
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpPut("brands/{id:guid}")]
    public async Task<ActionResult<ApiResponse<BrandDto>>> UpdateBrand(Guid id, BrandInput input, CancellationToken ct) => Ok(Envelope(await commands.UpdateBrandAsync(id, input, ct)));

    [AllowAnonymous, HttpGet("attributes")]
    public async Task<ActionResult<ApiResponse<PageDto<AttributeDto>>>> Attributes([FromQuery] CatalogListQuery query, CancellationToken ct) => Ok(Envelope(await queries.ListAttributesAsync(query, false, ct)));
    [AllowAnonymous, HttpGet("attributes/{id:guid}")]
    public async Task<ActionResult<ApiResponse<AttributeDto>>> Attribute(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetAttributeAsync(id, false, ct)));
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpGet("manage/attributes")]
    public async Task<ActionResult<ApiResponse<PageDto<AttributeDto>>>> ManageAttributes([FromQuery] CatalogListQuery query, CancellationToken ct) => Ok(Envelope(await queries.ListAttributesAsync(query, true, ct)));
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpGet("manage/attributes/{id:guid}")]
    public async Task<ActionResult<ApiResponse<AttributeDto>>> ManageAttribute(Guid id, CancellationToken ct) => Ok(Envelope(await queries.GetAttributeAsync(id, true, ct)));
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpPost("attributes")]
    public async Task<ActionResult<ApiResponse<AttributeDto>>> CreateAttribute(AttributeInput input, CancellationToken ct)
    {
        var dto = await commands.CreateAttributeAsync(input, ct); return CreatedAtAction(nameof(ManageAttribute), new { id = dto.Id }, Envelope(dto));
    }
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpPut("attributes/{id:guid}")]
    public async Task<ActionResult<ApiResponse<AttributeDto>>> UpdateAttribute(Guid id, AttributeInput input, CancellationToken ct) => Ok(Envelope(await commands.UpdateAttributeAsync(id, input, ct)));
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpPost("attributes/{id:guid}/values")]
    public async Task<ActionResult<ApiResponse<AttributeValueDto>>> AddValue(Guid id, AttributeValueInput input, CancellationToken ct)
    {
        var dto = await commands.AddAttributeValueAsync(id, input, ct); return CreatedAtAction(nameof(ManageAttribute), new { id }, Envelope(dto));
    }
    [Authorize(Policy = IdentityPermissions.ManageCatalog), HttpPut("attributes/{id:guid}/values/{valueId:guid}")]
    public async Task<ActionResult<ApiResponse<AttributeValueDto>>> UpdateValue(Guid id, Guid valueId, AttributeValueInput input, CancellationToken ct) => Ok(Envelope(await commands.UpdateAttributeValueAsync(id, valueId, input, ct)));
}
