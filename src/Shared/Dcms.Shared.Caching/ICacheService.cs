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

    /// <summary>
    /// Add to a counter, setting its expiry the first time it is created.
    ///
    /// <para>The expiry is what makes a counter usable as a window. Without it an `INCR` leaves
    /// a key behind forever, and a per-minute counter keyed by the minute leaves one per minute
    /// — which is a slow memory leak that only shows up months later.</para>
    ///
    /// <para>The TTL is set only on creation, so a window that is being written to does not keep
    /// sliding its own deadline forward and never close.</para>
    /// </summary>
    Task<long> IncrementAsync(string key, long by, TimeSpan ttl, CancellationToken ct = default);
}
