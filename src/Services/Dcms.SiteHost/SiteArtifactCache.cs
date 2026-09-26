using Dcms.Shared.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace Dcms.SiteHost;

/// <summary>
/// Remembers, per active build, which object a request path resolves to -- and for small files,
/// the bytes themselves -- so a page view stops costing 2-4 signed MinIO calls.
///
/// <para>No invalidation, by construction: every key starts with the build's
/// <c>ArtifactPrefix</c> (<c>tenant/site/buildId</c>), and a build's objects never change once it
/// is live. A publish produces a new prefix; the old entries simply stop being asked for and age
/// out. A purged or suspended site is still refused, because the route is resolved before this
/// cache is consulted. "No such file" is cached too, for the same reason it is permanent.</para>
///
/// <para>Bounded three ways, because <see cref="IObjectStorage"/> is explicit that buffering on a
/// request path is how delivery becomes a memory DoS: only files up to <see cref="MaxBodyBytes"/>
/// are held; the whole cache is capped at <see cref="Budget"/>; and at most <see cref="MaxFills"/>
/// buffers fill at once -- any other miss streams exactly as before.</para>
///
/// <para>The 2026-09-26 stress campaign put the site ceiling at ~310 req/s with MinIO the largest
/// CPU consumer on the host (1.1 cores) and ~11 % of site-host's CPU spent signing S3 requests.</para>
/// </summary>
public sealed class SiteArtifactCache : IDisposable
{
    public const long MaxBodyBytes = 256 * 1024;
    public const long Budget = 64L * 1024 * 1024;
    public const int MaxFills = 8;

    /// <param name="Key">The object the path resolved to, or null when nothing did.</param>
    /// <param name="Body">The bytes, when small enough to hold; otherwise it is streamed.</param>
    public sealed record Entry(string? Key, long Size, byte[]? Body);

    // A cache of its own: SizeLimit makes every entry declare a size, which the shared
    // IMemoryCache's other users do not.
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = Budget });
    private readonly SemaphoreSlim _fills = new(MaxFills);

    /// <summary>
    /// The entry for <paramref name="path"/> under a build, resolving and (if small) buffering it
    /// on a miss. <paramref name="candidates"/> are tried in order, as the endpoint always has.
    /// </summary>
    public async Task<Entry> GetAsync(
        IObjectStorage storage, string bucket, string artifactPrefix, string path,
        IReadOnlyList<string> candidates, bool indexFallback, CancellationToken ct)
    {
        var cacheKey = $"{artifactPrefix}\n{path}";
        if (_cache.TryGetValue(cacheKey, out Entry? entry) && entry is not null)
        {
            // Resolved earlier while every fill slot was busy: try to buffer it now.
            if (entry is { Key: not null, Body: null } && entry.Size <= MaxBodyBytes)
            {
                return await BufferAsync(storage, bucket, cacheKey, entry, ct);
            }
            return entry;
        }

        string? key = null;
        StoredObjectInfo? info = null;
        foreach (var candidate in candidates)
        {
            var candidateKey = $"{artifactPrefix}/{candidate}";
            if ((info = await storage.StatAsync(bucket, candidateKey, ct)) is not null)
            {
                key = candidateKey;
                break;
            }
        }
        if (key is null && indexFallback)
        {
            var indexKey = $"{artifactPrefix}/index.html";
            if ((info = await storage.StatAsync(bucket, indexKey, ct)) is not null)
            {
                key = indexKey;
            }
        }

        entry = new Entry(key, info?.Size ?? 0, null);
        if (key is not null && entry.Size <= MaxBodyBytes)
        {
            return await BufferAsync(storage, bucket, cacheKey, entry, ct);
        }
        Store(cacheKey, entry);
        return entry;
    }

    private async Task<Entry> BufferAsync(IObjectStorage storage, string bucket, string cacheKey, Entry entry, CancellationToken ct)
    {
        if (!_fills.Wait(0))
        {
            Store(cacheKey, entry);   // the resolution alone is still worth keeping
            return entry;
        }
        try
        {
            using var buffer = new MemoryStream((int)entry.Size);
            await storage.GetToAsync(bucket, entry.Key!, buffer, ct: ct);
            entry = entry with { Body = buffer.ToArray(), Size = buffer.Length };
        }
        finally
        {
            _fills.Release();
        }
        Store(cacheKey, entry);
        return entry;
    }

    private void Store(string cacheKey, Entry entry) =>
        _cache.Set(cacheKey, entry, new MemoryCacheEntryOptions
        {
            // Resolution-only entries still cost a key and a record; charge them 1 KB so a flood
            // of distinct missing paths cannot grow the cache past its budget.
            Size = Math.Max(1024, entry.Body?.Length ?? 0),
            // Only frees a superseded build's entries sooner; correctness never depends on it.
            SlidingExpiration = TimeSpan.FromMinutes(30),
        });

    public void Dispose()
    {
        _cache.Dispose();
        _fills.Dispose();
    }
}
