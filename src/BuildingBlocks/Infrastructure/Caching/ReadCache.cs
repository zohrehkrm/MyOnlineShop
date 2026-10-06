using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions;

namespace MyOnlineShop.BuildingBlocks.Infrastructure.Caching;

/// <summary>DTO cache-aside with bounded local contention and module-scoped generation invalidation.</summary>
public sealed class ReadCache(ICacheService cache, IOptions<RedisOptions> options)
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<T> GetAsync<T>(string scope, string resource, Func<CancellationToken, Task<T>> load, CancellationToken ct) where T : class
    {
        if (!options.Value.Enabled) return await load(ct);
        var gate = gates[(StringComparer.Ordinal.GetHashCode(scope + resource) & int.MaxValue) % gates.Length];
        await gate.WaitAsync(ct);
        try
        {
            var generationKey = CacheKeys.Generation(scope);
            var generation = await cache.GetAsync<string>(generationKey, ct);
            if (generation is null)
            {
                generation = Guid.NewGuid().ToString("N");
                await cache.SetAsync(generationKey, generation, TimeSpan.FromHours(24), ct);
                // Failure to establish a shared generation means bypass caching.
                if (await cache.GetAsync<string>(generationKey, ct) != generation) return await load(ct);
            }
            var key = CacheKeys.Entry(scope, generation, resource);
            var hit = await cache.GetAsync<T>(key, ct);
            if (hit is not null) return hit;
            var result = await load(ct);
            if (result is not null) await cache.SetAsync(key, result, TimeSpan.FromMinutes(options.Value.DefaultExpirationMinutes), ct);
            return result!;
        }
        finally { gate.Release(); }
    }

    // Invoke only AFTER successful SQL commit. Old in-flight reads write into an unreachable generation.
    public Task InvalidateAsync(string scope) => cache.SetAsync(CacheKeys.Generation(scope), Guid.NewGuid().ToString("N"), TimeSpan.FromHours(24), CancellationToken.None);
}
