using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyOnlineShop.BuildingBlocks.Abstractions;
using MyOnlineShop.BuildingBlocks.Infrastructure.Caching;
using Xunit;

namespace MyOnlineShop.Caching.Tests;

public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION_STRING")))
            Skip = "Live Redis unavailable; set REDIS_TEST_CONNECTION_STRING explicitly for isolated Redis integration tests.";
    }
}
public sealed class RedisIntegrationTests
{
    [RedisFact]
    public async Task Redis_dto_roundtrip_remove_and_ttl_in_isolated_namespace()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Redis:Enabled"] = "true",
            ["Redis:ConnectionString"] = Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION_STRING"),
            ["Redis:InstanceName"] = $"MyOnlineShopTests:{Guid.NewGuid():N}:"
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddReadCaching(config);
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();
        try
        {
            await cache.SetAsync("ttl", new[] { "DTO" }, TimeSpan.FromMilliseconds(250), default);
            Assert.Equal("DTO", Assert.Single((await cache.GetAsync<string[]>("ttl", default))!));
            await Task.Delay(350);
            Assert.Null(await cache.GetAsync<string[]>("ttl", default));
            await cache.SetAsync("remove", "value", TimeSpan.FromSeconds(5), default);
            await cache.RemoveAsync("remove", default);
            Assert.Null(await cache.GetAsync<string>("remove", default));
        }
        finally { await cache.RemoveAsync("ttl", default); await cache.RemoveAsync("remove", default); }
    }
}
