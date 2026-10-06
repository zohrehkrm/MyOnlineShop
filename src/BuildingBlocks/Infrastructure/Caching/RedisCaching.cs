using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions;
using StackExchange.Redis;

namespace MyOnlineShop.BuildingBlocks.Infrastructure.Caching;

public sealed class RedisOptions
{
    public bool Enabled { get; set; }
    public string ConnectionString { get; set; } = "";
    public string InstanceName { get; set; } = "MyOnlineShop:";
    public int DefaultExpirationMinutes { get; set; } = 2;
    public int OperationTimeoutMilliseconds { get; set; } = 500;
    public int FailureCooldownSeconds { get; set; } = 15;
    public int MaximumPayloadBytes { get; set; } = 262144;
}

public sealed class RedisCacheService(IDistributedCache cache, IOptions<RedisOptions> options,
    TimeProvider clock, ILogger<RedisCacheService> logger) : ICacheService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private long retryAt;
    private bool Available => options.Value.Enabled && clock.GetUtcNow().UtcTicks >= Interlocked.Read(ref retryAt);

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        if (!Available) return null;
        try
        {
            var bytes = await cache.GetAsync(key, ct).WaitAsync(Timeout, ct);
            if (bytes is null) return null;
            if (bytes.Length > options.Value.MaximumPayloadBytes) throw new JsonException();
            return JsonSerializer.Deserialize<T>(bytes, Json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (JsonException)
        {
            logger.LogWarning("Discarding incompatible cached DTO; schema {CacheSchema}", "v1");
            await RemoveAsync(key, ct);
            return null;
        }
        catch (Exception error) { Failure("get", error); return null; }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(value);
        if (expiration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(expiration));
        if (!Available) return;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (bytes.Length > options.Value.MaximumPayloadBytes)
            {
                logger.LogWarning("DTO exceeds cache payload limit {MaximumPayloadBytes}", options.Value.MaximumPayloadBytes);
                return;
            }
            await cache.SetAsync(key, bytes, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiration }, ct).WaitAsync(Timeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { Failure("set", error); }
    }

    public async Task RemoveAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Available) return;
        try { await cache.RemoveAsync(key, ct).WaitAsync(Timeout, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { Failure("remove", error); }
    }
    private TimeSpan Timeout => TimeSpan.FromMilliseconds(options.Value.OperationTimeoutMilliseconds);
    private void Failure(string operation, Exception error)
    {
        Interlocked.Exchange(ref retryAt, clock.GetUtcNow().AddSeconds(options.Value.FailureCooldownSeconds).UtcTicks);
        // Exception messages/objects may contain credentials or connection details. Log only the type.
        logger.LogWarning("Redis {CacheOperation} unavailable ({ErrorType}); using authoritative reads", operation, error.GetType().Name);
    }
}

public static class CachingDependencyInjection
{
    public static IServiceCollection AddReadCaching(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<RedisOptions>().Bind(configuration.GetSection("Redis"))
            .Validate(x => !x.Enabled || !string.IsNullOrWhiteSpace(x.ConnectionString), "Redis connection must be configured when enabled.")
            .Validate(x => x.DefaultExpirationMinutes is >= 1 and <= 10 && x.OperationTimeoutMilliseconds is >= 50 and <= 2000 &&
                x.FailureCooldownSeconds is >= 1 and <= 60 && x.MaximumPayloadBytes is >= 1024 and <= 1048576 &&
                !string.IsNullOrWhiteSpace(x.InstanceName), "Redis limits or instance namespace are invalid.").ValidateOnStart();
        services.AddStackExchangeRedisCache(settings =>
        {
            var configured = configuration.GetSection("Redis").Get<RedisOptions>() ?? new();
            settings.InstanceName = configured.InstanceName;
            // Disabled mode never invokes this lazy connection factory.
            settings.ConnectionMultiplexerFactory = async () =>
            {
                var connection = ConfigurationOptions.Parse(configured.ConnectionString);
                connection.AbortOnConnectFail = false;
                connection.ConnectRetry = 0;
                connection.ConnectTimeout = configured.OperationTimeoutMilliseconds;
                connection.AsyncTimeout = configured.OperationTimeoutMilliseconds;
                connection.SyncTimeout = configured.OperationTimeoutMilliseconds;
                connection.BacklogPolicy = BacklogPolicy.FailFast;
                return await ConnectionMultiplexer.ConnectAsync(connection);
            };
        });
        services.TryAddSingleton<ICacheService, RedisCacheService>();
        services.TryAddSingleton<ReadCache>();
        return services;
    }
}
