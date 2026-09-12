using System.Text.Json;
using StackExchange.Redis;

namespace Dcms.Shared.Caching;

public sealed class RedisCacheService(IConnectionMultiplexer redis) : ICacheService
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private IDatabase Db => redis.GetDatabase();

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var value = await Db.StringGetAsync(key);
        return value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>((byte[])value!);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        await Db.StringSetAsync(key, json, ttl ?? DefaultTtl);
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
        => Db.KeyDeleteAsync(key);

    public Task<long> IncrementAsync(string key, CancellationToken ct = default)
        => Db.StringIncrementAsync(key);

    public async Task<long> IncrementAsync(string key, long by, TimeSpan ttl, CancellationToken ct = default)
    {
        var value = await Db.StringIncrementAsync(key, by);

        // Only on creation. `INCR` on a missing key returns exactly `by`, which is how a
        // counter says "I am new" without a second round trip — and setting the expiry on every
        // write would push the deadline forward each time, so a window under continuous load
        // would never close.
        if (value == by) await Db.KeyExpireAsync(key, ttl);

        return value;
    }
}
