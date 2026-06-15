using Dcms.Shared.Contracts.Events;

namespace Dcms.Shared.Data.Media;

public enum MediaStatus
{
    Uploaded,
    Processing,
    Ready,
    Failed,
}

/// <summary>
/// An uploaded file. The original is stored in MinIO at
/// tenants/{tenantId}/{assetId}/original.{ext}; derived variants (webp ladder,
/// HLS renditions, …) are produced asynchronously by the media worker.
/// </summary>
public sealed class MediaAsset : TenantEntity
{
    public MediaCategory Category { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string OriginalKey { get; set; } = string.Empty;
    public MediaStatus Status { get; set; } = MediaStatus.Uploaded;
    public string? Error { get; set; }

    /// <summary>Probe metadata (dimensions, duration, …) as JSON.</summary>
    public string MetadataJson { get; set; } = "{}";

    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<MediaVariant> Variants { get; set; } = [];
}

/// <summary>A derived rendition of an asset (e.g. webp-640, thumb, hls-master).</summary>
public sealed class MediaVariant : TenantEntity
{
    public Guid AssetId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}
