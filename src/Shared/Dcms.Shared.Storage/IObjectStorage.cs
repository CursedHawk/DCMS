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
}

/// <summary>What a HEAD on an object tells us: enough to answer a range request without
/// transferring the body. Named to avoid colliding with MinIO's own <c>ObjectStat</c>.</summary>
public sealed record StoredObjectInfo(long Size, string? ContentType);

public static class StorageKeys
{
    public static string MediaOriginal(Guid tenantId, Guid assetId, string extension)
        => $"tenants/{tenantId}/{assetId}/original{extension}";

    public static string MediaVariant(Guid tenantId, Guid assetId, string variantFileName)
        => $"tenants/{tenantId}/{assetId}/{variantFileName}";

    public static string SiteArtifact(Guid tenantId, Guid siteId, Guid buildId, string relativePath)
        => $"{tenantId}/{siteId}/{buildId}/{relativePath}";

    /// <summary>Key for a staged static-files upload (Mode C), referenced by a build at publish time.</summary>
    public static string SiteBundleStaging(Guid tenantId, Guid siteId, Guid uploadId)
        => $"{tenantId}/{siteId}/_staging/{uploadId}.zip";
}
