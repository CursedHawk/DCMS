namespace Dcms.Shared.Data.Cms;

public enum ContentStatus
{
    Draft,
    Published,
    Archived,
}

/// <summary>
/// A piece of plugin content. Lifecycle: draft → published ↔ re-draft → archived.
/// The published payload is whichever version <see cref="PublishedVersionId"/>
/// points at; editing creates/updates the current draft version.
/// </summary>
public sealed class ContentItem : TenantEntity
{
    public Guid PluginInstanceId { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public ContentStatus Status { get; set; } = ContentStatus.Draft;

    public Guid? CurrentDraftVersionId { get; set; }
    public Guid? PublishedVersionId { get; set; }

    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }

    public List<ContentVersion> Versions { get; set; } = [];
}

/// <summary>Immutable snapshot of content data at a point in the edit history.</summary>
public sealed class ContentVersion : TenantEntity
{
    public Guid ItemId { get; set; }
    public int VersionNo { get; set; }

    /// <summary>The content payload as JSON (shape defined by the plugin content type).</summary>
    public string DataJson { get; set; } = "{}";

    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
