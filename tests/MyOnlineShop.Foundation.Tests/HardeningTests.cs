using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MyOnlineShop.Foundation.Tests;

public sealed class HardeningTests
{
    [Theory]
    [InlineData("throw", "secret-password")]
    [InlineData("db-error", "private-secret")]
    public async Task Production_exception_pipeline_omits_sensitive_details_from_response_and_logs(string action, string secret)
    {
        using var original = new FoundationFactory(); var logs = new Logs();
        using var factory = original.WithWebHostBuilder(builder => { builder.UseEnvironment("Production"); builder.ConfigureLogging(logging => logging.AddProvider(logs)); });
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "production-error");
        using var response = await client.GetAsync("/foundation-probe/" + action);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync(); using var json = JsonDocument.Parse(body);
        Assert.Equal("production-error", json.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("internal_error", json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(secret, body); Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, string.Join('\n', logs.Values));
        Assert.Contains(logs.Values, value => value.Contains("Request failed with", StringComparison.Ordinal));
    }
    private sealed class Logs : ILoggerProvider
    {
        public ConcurrentQueue<string> Values { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(Values);
        public void Dispose() { }
        private sealed class Logger(ConcurrentQueue<string> values) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
                values.Enqueue(formatter(state, error) + error?.ToString());
        }
    }
}
