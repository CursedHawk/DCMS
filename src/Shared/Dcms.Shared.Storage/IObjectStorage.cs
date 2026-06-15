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
}

public static class StorageKeys
{
    public static string MediaOriginal(Guid tenantId, Guid assetId, string extension)
        => $"tenants/{tenantId}/{assetId}/original{extension}";

    public static string MediaVariant(Guid tenantId, Guid assetId, string variantFileName)
        => $"tenants/{tenantId}/{assetId}/{variantFileName}";

    public static string SiteArtifact(Guid tenantId, Guid siteId, Guid buildId, string relativePath)
        => $"{tenantId}/{siteId}/{buildId}/{relativePath}";
}
