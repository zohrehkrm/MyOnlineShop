using MyOnlineShop.BuildingBlocks.Abstractions;

namespace MyOnlineShop.Foundation;

internal sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    public string CorrelationId => accessor.HttpContext?.TraceIdentifier ?? string.Empty;
}
