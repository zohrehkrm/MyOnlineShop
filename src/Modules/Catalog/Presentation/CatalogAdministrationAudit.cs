using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Catalog.Contracts;

namespace MyOnlineShop.Catalog.Presentation;

public sealed class CatalogAdministrationAudit(ILogger<CatalogAdministrationAudit> logger, TimeProvider time) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.Request.Method is not ("POST" or "PUT")) { await next(); return; }
        if (context.HttpContext.User.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(context.HttpContext.User.FindFirst("sub")?.Value, out var actor) || actor == Guid.Empty)
        { context.Result = new UnauthorizedResult(); return; }
        var result = await next();
        if (result.Exception is not null || result.Result is not ObjectResult response || (response.StatusCode ?? 200) >= 400) return;
        Guid? target = response.Value switch
        {
            ApiResponse<ProductDto> value => value.Data.Id,
            ApiResponse<VariantDto> value => value.Data.Id,
            ApiResponse<CategoryDto> value => value.Data.Id,
            ApiResponse<BrandDto> value => value.Data.Id,
            ApiResponse<AttributeDto> value => value.Data.Id,
            ApiResponse<AttributeValueDto> value => value.Data.Id,
            _ => null
        };
        if (target is null) return;
        logger.LogInformation("Catalog administration {Operation} by {ActorId} on {TargetId} at {OccurredAtUtc}; correlation {CorrelationId}",
            context.ActionDescriptor.RouteValues["action"], actor, target, time.GetUtcNow(), context.HttpContext.TraceIdentifier);
    }
}
