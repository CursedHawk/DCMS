using Dcms.Shared.Storage;
using Dcms.SiteHost;

namespace Dcms.UnitTests.SiteHost;

public class SiteArtifactCacheTests
{
    private const string Bucket = "sites";
    private const string Build = "t/s/build-1";

    /// <summary>In-memory storage that counts the calls the cache exists to avoid.</summary>
    private sealed class CountingStorage(Dictionary<string, byte[]> objects) : IObjectStorage
    {
        public int Stats;
        public int Gets;

        public Task<StoredObjectInfo?> StatAsync(string bucket, string key, CancellationToken ct = default)
        {
            Stats++;
            return Task.FromResult(objects.TryGetValue(key, out var o) ? new StoredObjectInfo(o.Length, null) : null);
        }

        public async Task GetToAsync(string bucket, string key, Stream destination, long? offset = null, long? length = null, CancellationToken ct = default)
        {
            Gets++;
            await destination.WriteAsync(objects[key], ct);
        }

        public Task PutAsync(string bucket, string key, Stream content, long size, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> GetAsync(string bucket, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string bucket, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<string> ListKeysAsync(string bucket, string prefix, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_small_file_is_fetched_once_then_served_from_memory()
    {
        var storage = new CountingStorage(new() { [$"{Build}/about.html"] = "<h1>hi</h1>"u8.ToArray() });
        using var cache = new SiteArtifactCache();

        var first = await cache.GetAsync(storage, Bucket, Build, "about", ["about.html"], indexFallback: true, CancellationToken.None);
        var second = await cache.GetAsync(storage, Bucket, Build, "about", ["about.html"], indexFallback: true, CancellationToken.None);

        second.Body.Should().Equal("<h1>hi</h1>"u8.ToArray());
        first.Key.Should().Be($"{Build}/about.html");
        (storage.Stats, storage.Gets).Should().Be((1, 1));
    }

    [Fact]
    public async Task Candidates_and_the_index_fallback_resolve_once_and_misses_are_remembered()
    {
        var storage = new CountingStorage(new() { [$"{Build}/index.html"] = "spa"u8.ToArray() });
        using var cache = new SiteArtifactCache();

        // Page route: exact, then wildcard, then the SPA index.
        for (var i = 0; i < 3; i++)
        {
            (await cache.GetAsync(storage, Bucket, Build, "events/x", ["events_x.html", "events_@.html"], indexFallback: true, CancellationToken.None))
                .Key.Should().Be($"{Build}/index.html");
        }
        // An asset never falls back to index.html, and its absence is permanent for this build.
        for (var i = 0; i < 3; i++)
        {
            (await cache.GetAsync(storage, Bucket, Build, "missing.css", ["missing.css"], indexFallback: false, CancellationToken.None))
                .Key.Should().BeNull();
        }

        storage.Stats.Should().Be(4, "three stats for the page route and one for the asset, each once");
    }

    [Fact]
    public async Task A_large_file_is_resolved_but_never_held()
    {
        var big = new byte[SiteArtifactCache.MaxBodyBytes + 1];
        var storage = new CountingStorage(new() { [$"{Build}/video.mp4"] = big });
        using var cache = new SiteArtifactCache();

        var entry = await cache.GetAsync(storage, Bucket, Build, "video.mp4", ["video.mp4"], indexFallback: false, CancellationToken.None);
        await cache.GetAsync(storage, Bucket, Build, "video.mp4", ["video.mp4"], indexFallback: false, CancellationToken.None);

        entry.Body.Should().BeNull();
        entry.Size.Should().Be(big.Length);
        (storage.Stats, storage.Gets).Should().Be((1, 0));
    }

    [Fact]
    public async Task A_new_build_is_a_different_key()
    {
        var storage = new CountingStorage(new()
        {
            ["t/s/build-1/index.html"] = "old"u8.ToArray(),
            ["t/s/build-2/index.html"] = "new"u8.ToArray(),
        });
        using var cache = new SiteArtifactCache();

        await cache.GetAsync(storage, Bucket, "t/s/build-1", "", ["index.html"], indexFallback: true, CancellationToken.None);
        (await cache.GetAsync(storage, Bucket, "t/s/build-2", "", ["index.html"], indexFallback: true, CancellationToken.None))
            .Body.Should().Equal("new"u8.ToArray());
    }
}
