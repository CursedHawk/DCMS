namespace Dcms.Shared.Storage;

/// <summary>
/// Thin wrapper over MinIO. Object keys follow the convention
/// tenants/{tenantId}/{assetId}/{variant} inside per-concern buckets.
/// </summary>
public interface IObjectStorage
{
    Task PutAsync(string bucket, string key, Stream content, long size, string contentType, CancellationToken ct = default);

    /// <summary>
    /// Downloads the whole object into memory. Only for callers that genuinely need a seekable
    /// stream over the entire object (the publish consumer reading a zip's central directory).
    /// <b>Never on a request path</b> — see <see cref="GetToAsync"/>: buffering here makes memory
    /// use scale with concurrency × file size, which is how a delivery endpoint becomes a DoS.
    /// </summary>
    Task<Stream> GetAsync(string bucket, string key, CancellationToken ct = default);

    /// <summary>
    /// Copies the object — or, when <paramref name="offset"/> and <paramref name="length"/> are
    /// given, just that byte range — straight to <paramref name="destination"/>, holding only the
    /// copy buffer. This is what request paths serve from.
    /// </summary>
    Task GetToAsync(
        string bucket, string key, Stream destination,
        long? offset = null, long? length = null, CancellationToken ct = default);

    /// <summary>Size and content type without transferring the body; null when absent.</summary>
    Task<StoredObjectInfo?> StatAsync(string bucket, string key, CancellationToken ct = default);

    /// <summary>
    /// The object's body as a stream, with its size and content type, from one request; null when
    /// absent. <paramref name="range"/> asks for that slice only. What request paths serve from:
    /// a stat and then a get is five round trips through the MinIO SDK (2026-10-23).
    ///
    /// <para>The default buffers and exists for in-memory test doubles only.</para>
    /// </summary>
    async Task<ObjectRead?> OpenReadAsync(string bucket, string key, ByteRange? range = null, CancellationToken ct = default)
    {
        if (await StatAsync(bucket, key, ct) is not { } info)
        {
            return null;
        }
        var body = new MemoryStream();
        await GetToAsync(bucket, key, body, range?.Start, range?.Length, ct);
        body.Position = 0;
        return new ObjectRead(body, info.Size, info.ContentType);
    }

    Task<bool> ExistsAsync(string bucket, string key, CancellationToken ct = default);
    Task DeleteAsync(string bucket, string key, CancellationToken ct = default);

    /// <summary>Every object key under a prefix, recursively.</summary>
    IAsyncEnumerable<string> ListKeysAsync(string bucket, string prefix, CancellationToken ct = default);

    /// <summary>
    /// Deletes every object under a prefix and returns how many were removed. The
    /// key convention is hierarchical ({tenantId}/{siteId}/…), so this is how a
    /// site's or a tenant's artifacts are reclaimed when the owning row is deleted.
    /// Best-effort and re-runnable: deleting an already-empty prefix is a no-op.
    /// </summary>
    Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken ct = default);

    /// <summary>Total bytes of every object whose key starts with <paramref name="prefix"/>.</summary>
    Task<long> SizeOfPrefixAsync(string bucket, string prefix, CancellationToken ct = default);
}

/// <summary>What a HEAD on an object tells us: enough to answer a range request without
/// transferring the body. Named to avoid colliding with MinIO's own <c>ObjectStat</c>.</summary>
public sealed record StoredObjectInfo(long Size, string? ContentType);

/// <summary>An open object: <see cref="Body"/> is the requested bytes, <see cref="Size"/> the whole
/// object's. Dispose it to release the connection.</summary>
public sealed record ObjectRead(Stream Body, long Size, string? ContentType) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Body.DisposeAsync();
}

public static class StorageKeys
{
    public static string MediaOriginal(Guid tenantId, Guid assetId, string extension)
        => $"tenants/{tenantId}/{assetId}/original{extension}";

    public static string MediaVariant(Guid tenantId, Guid assetId, string variantFileName)
        => $"tenants/{tenantId}/{assetId}/{variantFileName}";

    /// <summary>Everything stored for one asset: its original, its variants and its HLS tree.</summary>
    public static string MediaAssetPrefix(Guid tenantId, Guid assetId)
        => $"tenants/{tenantId}/{assetId}/";

    public static string SiteArtifact(Guid tenantId, Guid siteId, Guid buildId, string relativePath)
        => $"{tenantId}/{siteId}/{buildId}/{relativePath}";

    /// <summary>Key for a staged static-files upload (Mode C), referenced by a build at publish time.</summary>
    public static string SiteBundleStaging(Guid tenantId, Guid siteId, Guid uploadId)
        => $"{tenantId}/{siteId}/_staging/{uploadId}.zip";
}
