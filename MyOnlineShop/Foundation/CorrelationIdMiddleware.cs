namespace MyOnlineShop.Foundation;

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var values = context.Request.Headers[HeaderName];
        var candidate = values.Count == 1 ? values[0] : null;
        var correlationId = IsValid(candidate) ? candidate! : Guid.NewGuid().ToString("N");
        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["RequestId"] = System.Diagnostics.Activity.Current?.Id ?? correlationId
        });
        await next(context);
        logger.LogInformation("HTTP {Method} completed with {StatusCode}",
            context.Request.Method, context.Response.StatusCode);
    }

    private static bool IsValid(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
