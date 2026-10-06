using System.Linq.Expressions;
using Dcms.Shared.Contracts.Events;

namespace Dcms.Shared.Data.Media;

/// <summary>
/// Which originals a tenant may delete to free space, and what is served as "original" after.
///
/// <para>Pages store an asset as <c>/api/media/{id}/original</c>, so deleting an original is only
/// safe where a variant can answer that URL in its place: a picture by its largest WebP, a track
/// by its AAC copy. Video is not offered — site blocks play the original in a plain
/// <c>&lt;video&gt;</c>, and its HLS copy plays natively only in Safari. Files and SVG have no
/// copy at all.</para>
/// </summary>
public static class MediaOriginals
{
    /// <summary>
    /// Whether the original can go without breaking anything that uses the asset. An expression,
    /// so the list and the delete endpoint filter in the database by the same rule.
    /// </summary>
    public static readonly Expression<Func<MediaAsset, bool>> Deletable = a =>
        a.Status == MediaStatus.Ready
        && a.OriginalDeletedAt == null
        && ((a.Category == MediaCategory.Image && a.Variants.Any(v => v.Kind.StartsWith("webp-")))
            || (a.Category == MediaCategory.Audio && a.Variants.Any(v => v.Kind == "aac")));

    private static readonly Func<MediaAsset, bool> DeletableInMemory = Deletable.Compile();

    /// <summary><see cref="Deletable"/> for an asset already loaded with its variants.</summary>
    public static bool CanDelete(MediaAsset asset) => DeletableInMemory(asset);

    /// <summary>The variant that answers for the original once it is gone, or null if none can.</summary>
    public static MediaVariant? StandIn(MediaCategory category, IEnumerable<MediaVariant> variants) => category switch
    {
        MediaCategory.Image => variants.Where(v => v.Kind.StartsWith("webp-", StringComparison.Ordinal)).MaxBy(v => v.Width ?? 0),
        MediaCategory.Audio => variants.FirstOrDefault(v => v.Kind == "aac"),
        _ => null,
    };
}
