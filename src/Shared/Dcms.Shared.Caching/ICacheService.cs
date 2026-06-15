namespace Dcms.Shared.Caching;

/// <summary>
/// JSON value cache over Redis. Key conventions:
/// t:{tenantId}:c:{instanceId}:{contentType}:{slug} for items,
/// t:{tenantId}:c:{instanceId}:gen for the per-instance generation counter
/// folded into list-query key hashes (O(1) list invalidation).
/// </summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task<long> IncrementAsync(string key, CancellationToken ct = default);
}
