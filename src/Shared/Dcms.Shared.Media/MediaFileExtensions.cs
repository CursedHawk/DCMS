namespace Dcms.Shared.Media;

/// <summary>
/// The canonical file extension for a stored content type.
///
/// <para>Lives here rather than in admin-api because the ingest path and the media worker both
/// need it and must agree: ingest names the original object from the <i>sniffed</i> type, and
/// the worker renames it if the re-encode it performs settles on a different one. Two copies
/// of this table drifting apart would leave an asset whose row and whose object key disagree
/// about what it is.</para>
/// </summary>
public static class MediaFileExtensions
{
    public static string ToExtension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        "video/mp4" => ".mp4",
        "audio/mpeg" => ".mp3",
        "audio/wav" => ".wav",
        "audio/ogg" => ".ogg",
        "application/pdf" => ".pdf",
        "application/zip" => ".zip",
        _ => ".bin",
    };
}
