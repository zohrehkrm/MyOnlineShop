using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.Catalog.Contracts;
using MyOnlineShop.Catalog.Presentation;
using Xunit;

namespace MyOnlineShop.Catalog.Tests;

public sealed class AdministrationAuditTests
{
    [Fact]
    public void Administrative_API_routes_have_no_duplicate_method_and_pattern()
    {
        using var factory = new CatalogApiFactory(); using var client = factory.Client();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        var routes = endpoints.Where(value => value.RoutePattern.RawText?.StartsWith("api/v1/", StringComparison.Ordinal) == true)
            .SelectMany(value => (value.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (method, pattern: value.RoutePattern.RawText!.ToLowerInvariant()))).ToArray();
        Assert.NotEmpty(routes);
        Assert.All(routes.GroupBy(value => value), group => Assert.Single(group));
    }
    [Fact]
    public async Task Successful_management_logs_only_server_actor_target_time_and_correlation()
    {
        var actor = Guid.NewGuid(); var target = Guid.NewGuid(); var logs = new CaptureLogger();
        var context = Context(actor.ToString());
        var audit = new CatalogAdministrationAudit(logs, TimeProvider.System);
        await audit.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, [], new object())
        { Result = new CreatedResult("/category", new ApiResponse<CategoryDto>(new(target, "Sensitive name", "CODE", null, true), "audit-test")) }));
        var values = Assert.Single(logs.Events);
        Assert.Equal(actor, values["ActorId"]); Assert.Equal(target, values["TargetId"]);
        Assert.Equal("CreateCategory", values["Operation"]); Assert.Equal("audit-test", values["CorrelationId"]);
        Assert.IsType<DateTimeOffset>(values["OccurredAtUtc"]);
        Assert.DoesNotContain(values.Values, value => value?.ToString()?.Contains("Sensitive") == true);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Invalid_actor_cannot_execute_a_write(string actor)
    {
        var context = Context(actor); var logs = new CaptureLogger(); var called = false;
        await new CatalogAdministrationAudit(logs, TimeProvider.System).OnActionExecutionAsync(context, () =>
        { called = true; return Task.FromResult(new ActionExecutedContext(context, [], new object())); });
        Assert.False(called); Assert.IsType<UnauthorizedResult>(context.Result); Assert.Empty(logs.Events);
    }

    [Fact]
    public async Task Failed_write_does_not_log_success_and_anonymous_get_remains_public()
    {
        var context = Context(Guid.NewGuid().ToString()); var logs = new CaptureLogger();
        var audit = new CatalogAdministrationAudit(logs, TimeProvider.System);
        await audit.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, [], new object()) { Result = new BadRequestObjectResult("invalid") }));
        Assert.Empty(logs.Events);
        context.HttpContext.Request.Method = "GET"; context.HttpContext.User = new(); var called = false;
        await audit.OnActionExecutionAsync(context, () => { called = true; return Task.FromResult(new ActionExecutedContext(context, [], new object())); });
        Assert.True(called); Assert.Empty(logs.Events);
    }

    private static ActionExecutingContext Context(string actor)
    {
        var http = new DefaultHttpContext { TraceIdentifier = "audit-test", User = new(new ClaimsIdentity([new Claim("sub", actor)], "Test")) };
        http.Request.Method = "POST";
        var action = new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor(); action.RouteValues["action"] = "CreateCategory";
        return new(new ActionContext(http, new RouteData(), action), [], new Dictionary<string, object?>(), new object());
    }
    private sealed class CaptureLogger : ILogger<CatalogAdministrationAudit>
    {
        public List<Dictionary<string, object?>> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
            Events.Add(((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(value => value.Key, value => value.Value));
    }
}
