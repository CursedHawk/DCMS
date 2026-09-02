namespace Dcms.Shared.Data.Social;

/// <summary>
/// Maps a Meta media id to the DCMS asset we mirrored it into.
///
/// <para>This is what makes a 15-minute poll cheap: without it every pass would
/// re-download and re-process every image in the configured window. It is also the
/// ownership record that lets retention delete a mirrored asset when an item falls
/// past its cap — only assets listed here were created by sync, so an admin's own
/// upload can never be collected by that sweep.</para>
/// </summary>
public sealed class MetaMediaMap : TenantEntity
{
    public Guid ConnectionId { get; set; }

    /// <summary>
    /// Meta's media id. Carousel children are suffixed (<c>{parentId}:{childId}</c>) so
    /// each mirrored file has its own row.
    /// </summary>
    public string ExternalMediaId { get; set; } = string.Empty;

    public Guid MediaAssetId { get; set; }
    public DateTimeOffset MirroredAt { get; set; } = DateTimeOffset.UtcNow;
}
