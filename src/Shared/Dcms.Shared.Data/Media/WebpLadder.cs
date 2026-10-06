namespace Dcms.Shared.Data.Media;

/// <summary>
/// The responsive WebP variants a picture is made at (<c>webp-320</c> … <c>webp-1920</c>, by the
/// media worker), and how a request for a width an asset was not made at is answered.
/// </summary>
public static class WebpLadder
{
    /// <summary>The width a <c>webp-N</c> variant name asks for, or null for any other name.</summary>
    public static int? WidthOf(string kind) =>
        kind.StartsWith("webp-", StringComparison.Ordinal) && int.TryParse(kind.AsSpan(5), out var w) && w > 0 ? w : null;

    /// <summary>
    /// The variant to serve for a WebP width the asset lacks: the narrowest at least as wide, else
    /// the widest there is — never a blurrier picture than was asked for when a sharper one exists.
    /// Null when the request is not a WebP width or the asset has none.
    /// </summary>
    public static string? Nearest(string requested, IEnumerable<string> available)
    {
        var wanted = WidthOf(requested);
        if (wanted is null) return null;
        var ladder = available.Select(k => (Kind: k, Width: WidthOf(k))).Where(x => x.Width is not null).ToList();
        return ladder.Where(x => x.Width >= wanted).OrderBy(x => x.Width).Select(x => x.Kind).FirstOrDefault()
            ?? ladder.OrderByDescending(x => x.Width).Select(x => x.Kind).FirstOrDefault();
    }
}
