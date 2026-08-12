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
/// A named, optionally nested folder the tenant organizes media into. Purely an
/// organizational grouping over <see cref="MediaAsset"/>; deleting a folder does
/// not delete its assets (they fall back to the root / "unfiled" view).
/// </summary>
public sealed class MediaFolder : TenantEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Parent folder id for nesting; null = a top-level folder.</summary>
    public Guid? ParentId { get; set; }

    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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

    /// <summary>Optional folder the tenant filed this asset under; null = unfiled (root).</summary>
    public Guid? FolderId { get; set; }

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
