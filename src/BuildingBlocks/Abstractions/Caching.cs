namespace MyOnlineShop.BuildingBlocks.Abstractions;

/// <summary>Optional optimization. A miss (including an unavailable cache) requires an authoritative read.</summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct) where T : class;
    Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken ct) where T : class;
    Task RemoveAsync(string key, CancellationToken ct);
}

public static class CacheKeys
{
    public static string Generation(string scope) => $"v1:{scope}:generation";
    public static string Entry(string scope, string generation, string resource) => $"v1:{scope}:{generation}:{resource}";
    public static string Detail(string kind, Guid id, bool management = false) => $"{kind}:{id:N}:{(management ? "management" : "public")}";
}
