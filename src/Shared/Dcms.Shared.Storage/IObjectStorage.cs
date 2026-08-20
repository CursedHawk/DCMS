namespace Dcms.Shared.Storage;

/// <summary>
/// Thin wrapper over MinIO. Object keys follow the convention
/// tenants/{tenantId}/{assetId}/{variant} inside per-concern buckets.
/// </summary>
public interface IObjectStorage
{
    Task PutAsync(string bucket, string key, Stream content, long size, string contentType, CancellationToken ct = default);
    Task<Stream> GetAsync(string bucket, string key, CancellationToken ct = default);
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
