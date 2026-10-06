using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Infrastructure.Caching;
using MyOnlineShop.Catalog.Contracts;
using Xunit;

namespace MyOnlineShop.Caching.Tests;

public sealed class CacheTests
{
    [Fact]
    public void Keys_are_versioned_stable_and_separate_visibility_and_modules()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"product:{id:N}:public", CacheKeys.Detail("product", id));
        Assert.NotEqual(CacheKeys.Detail("product", id), CacheKeys.Detail("product", id, true));
        Assert.Equal("v1:catalog:g:product", CacheKeys.Entry("catalog", "g", "product"));
        Assert.NotEqual(CacheKeys.Generation("catalog"), CacheKeys.Generation("shipping"));
    }
    [Fact]
    public async Task Dto_round_trip_and_remove()
    {
        var h = new Harness(); var dto = Dto();
        await h.Cache.SetAsync("key", dto, TimeSpan.FromMinutes(2), default);
        var copy = await h.Cache.GetAsync<CategoryDto>("key", default);
        Assert.Equal(dto, copy); Assert.NotSame(dto, copy);
        await h.Cache.RemoveAsync("key", default);
        Assert.Null(await h.Cache.GetAsync<CategoryDto>("key", default));
    }
    [Fact]
    public async Task Miss_loads_once_then_hits()
    {
        var h = new Harness(); var calls = 0;
        Task<CategoryDto> Load(CancellationToken _) { calls++; return Task.FromResult(Dto()); }
        var first = await h.Read.GetAsync("catalog", "category", Load, default);
        Assert.Equal(first, await h.Read.GetAsync("catalog", "category", Load, default));
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task Expiration_forces_database_reload()
    {
        var h = new Harness(); var calls = 0;
        Task<CategoryDto> Load(CancellationToken _) { calls++; return Task.FromResult(Dto()); }
        await h.Read.GetAsync("catalog", "category", Load, default);
        h.Clock.Advance(TimeSpan.FromMinutes(3));
        await h.Read.GetAsync("catalog", "category", Load, default);
        Assert.Equal(2, calls);
        Assert.Contains(h.Backend.Entries.Values, x => x.Expires == h.Clock.GetUtcNow().AddMinutes(2));
    }
    [Fact]
    public async Task Invalidation_is_module_scoped()
    {
        var h = new Harness(); var calls = 0;
        Task<CategoryDto> Load(CancellationToken _) { calls++; return Task.FromResult(Dto()); }
        await h.Read.GetAsync("catalog", "category", Load, default);
        await h.Read.GetAsync("shipping", "methods", Load, default);
        await h.Read.InvalidateAsync("catalog");
        await h.Read.GetAsync("catalog", "category", Load, default);
        await h.Read.GetAsync("shipping", "methods", Load, default);
        Assert.Equal(3, calls);
    }
    [Fact]
    public async Task Inflight_read_cannot_repopulate_invalidated_generation()
    {
        var h = new Harness(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = Dto(); var fresh = Dto();
        var pending = h.Read.GetAsync("catalog", "category", async _ => { started.SetResult(); await release.Task; return stale; }, default);
        await started.Task; await h.Read.InvalidateAsync("catalog"); release.SetResult(); await pending;
        Assert.Equal(fresh, await h.Read.GetAsync("catalog", "category", _ => Task.FromResult(fresh), default));
    }
    [Fact]
    public async Task Concurrent_misses_have_one_local_loader()
    {
        var h = new Harness(); var calls = 0;
        var tasks = Enumerable.Range(0, 20).Select(_ => h.Read.GetAsync("catalog", "category", async token =>
        { Interlocked.Increment(ref calls); await Task.Yield(); return Dto(); }, default));
        await Task.WhenAll(tasks); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task Unavailable_redis_falls_back_and_suppresses_repeated_attempts()
    {
        var h = new Harness(); h.Backend.Fail = true; var calls = 0;
        Task<CategoryDto> Load(CancellationToken _) { calls++; return Task.FromResult(Dto()); }
        await h.Read.GetAsync("catalog", "category", Load, default);
        await h.Read.GetAsync("catalog", "category", Load, default);
        await h.Read.InvalidateAsync("catalog");
        Assert.Equal(2, calls); Assert.Equal(1, h.Backend.Gets);
        Assert.Contains("authoritative reads", Assert.Single(h.Log.Messages));
        Assert.DoesNotContain("secret-password", string.Join(" ", h.Log.Messages));
        h.Backend.Fail = false; h.Clock.Advance(TimeSpan.FromSeconds(16));
        await h.Read.GetAsync("catalog", "category", Load, default);
        await h.Read.GetAsync("catalog", "category", Load, default);
        Assert.Equal(3, calls);
    }
    [Fact]
    public async Task Corruption_is_removed_and_database_value_replaces_it()
    {
        var h = new Harness(); await h.Cache.SetAsync("key", Dto(), TimeSpan.FromMinutes(2), default);
        h.Backend.Entries["key"] = (Encoding.UTF8.GetBytes("broken-json"), h.Clock.GetUtcNow().AddMinutes(2));
        Assert.Null(await h.Cache.GetAsync<CategoryDto>("key", default));
        Assert.False(h.Backend.Entries.ContainsKey("key")); Assert.Single(h.Log.Messages);
    }
    [Fact]
    public async Task Null_is_not_cached_and_invalid_ttl_is_rejected()
    {
        var h = new Harness();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Cache.SetAsync<CategoryDto>("key", null!, TimeSpan.FromMinutes(1), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => h.Cache.SetAsync("key", Dto(), TimeSpan.Zero, default));
        Assert.Empty(h.Backend.Entries);
    }
    [Fact]
    public async Task Oversized_payload_is_not_cached()
    {
        var h = new Harness();
        await h.Cache.SetAsync("key", new string('x', 300000), TimeSpan.FromMinutes(2), default);
        Assert.Empty(h.Backend.Entries); Assert.Single(h.Log.Messages);
    }
    [Fact]
    public async Task Cancellation_is_propagated()
    {
        var h = new Harness(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Cache.GetAsync<CategoryDto>("key", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Read.GetAsync("catalog", "category", _ => Task.FromResult(Dto()), cts.Token));
        Assert.Empty(h.Log.Messages);
    }
    [Fact]
    public async Task Disabled_configuration_never_creates_a_redis_connection()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddReadCaching(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Assert.Null(await provider.GetRequiredService<ICacheService>().GetAsync<CategoryDto>("key", default));
        Assert.Equal("database", await provider.GetRequiredService<ReadCache>().GetAsync("catalog", "key", _ => Task.FromResult("database"), default));
    }
    [Fact]
    public async Task Timeout_is_bounded_and_falls_back()
    {
        var h = new Harness(); h.Backend.Hang = true;
        Assert.Equal("database", await h.Read.GetAsync("catalog", "key", _ => Task.FromResult("database"), default).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Single(h.Log.Messages);
    }
    [Fact]
    public async Task Loader_errors_are_not_cached_or_hidden()
    {
        var h = new Harness();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Read.GetAsync<CategoryDto>("catalog", "key", _ => throw new InvalidOperationException("database failure"), default));
        Assert.DoesNotContain(h.Backend.Entries.Keys, x => x.Contains(":key"));
    }
    private static CategoryDto Dto() => new(Guid.NewGuid(), "Category", "CATEGORY", null, true);
}

internal sealed class Harness
{
    public TestClock Clock { get; } = new();
    public FakeDistributedCache Backend { get; }
    public TestLog Log { get; } = new();
    public RedisCacheService Cache { get; }
    public ReadCache Read { get; }
    public Harness()
    {
        Backend = new(Clock);
        var options = Options.Create(new RedisOptions { Enabled = true, OperationTimeoutMilliseconds = 50 });
        Cache = new(Backend, options, Clock, Log); Read = new(Cache, options);
    }
}
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan amount) => now += amount;
}
internal sealed class FakeDistributedCache(TestClock clock) : IDistributedCache
{
    public ConcurrentDictionary<string, (byte[] Bytes, DateTimeOffset Expires)> Entries { get; } = new();
    public bool Fail { get; set; }
    public bool Hang { get; set; }
    public int Gets;
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        Interlocked.Increment(ref Gets);
        if (Fail) throw new IOException("secret-password");
        if (Hang) return new TaskCompletionSource<byte[]?>().Task;
        return Task.FromResult(Entries.TryGetValue(key, out var value) && value.Expires > clock.GetUtcNow() ? value.Bytes : null);
    }
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    { if (Fail) throw new IOException("secret-password"); Entries[key] = (value, clock.GetUtcNow() + options.AbsoluteExpirationRelativeToNow!.Value); return Task.CompletedTask; }
    public Task RemoveAsync(string key, CancellationToken token = default) { if (Fail) throw new IOException("secret-password"); Entries.TryRemove(key, out _); return Task.CompletedTask; }
    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    public byte[]? Get(string key) => throw new NotSupportedException();
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
    public void Remove(string key) => throw new NotSupportedException();
    public void Refresh(string key) => throw new NotSupportedException();
}
internal sealed class TestLog : ILogger<RedisCacheService>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, error));
}
