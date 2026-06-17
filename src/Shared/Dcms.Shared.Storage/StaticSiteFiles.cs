namespace Dcms.Shared.Storage;

/// <summary>
/// Shared helpers for the "static files" hosting mode (Mode C): safe path
/// normalization for uploaded/zip entries, web content-type mapping, and the
/// guard-rail limits. Used by admin-api (ingest), site-builder (extraction) and
/// site-host (serving) so the rules can't drift between them.
/// </summary>
public static class StaticSiteFiles
{
    /// <summary>Max size of a single uploaded request body (compressed).</summary>
    public const long MaxUploadBytes = 100L * 1024 * 1024;

    /// <summary>Max total size of all files after decompression (zip-bomb guard).</summary>
    public const long MaxUncompressedBytes = 300L * 1024 * 1024;

    /// <summary>Max size of any single file in the bundle.</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>Max number of files in a bundle.</summary>
    public const int MaxFileCount = 5000;

    /// <summary>
    /// Normalize a zip entry / upload relative path to a safe, forward-slash
    /// relative path. Returns null for directory entries, empty paths, absolute
    /// paths, or anything attempting traversal (zip-slip).
    /// </summary>
    public static string? NormalizeEntryPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var value = raw.Replace('\\', '/').Trim().TrimStart('/');
        if (value.Length == 0 || value.EndsWith('/'))
        {
            return null; // directory entry
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cleaned = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == ".." || segment.Contains(':'))
            {
                return null; // traversal or drive-qualified → reject
            }
            cleaned.Add(segment);
        }
        return cleaned.Count == 0 ? null : string.Join('/', cleaned);
    }

    /// <summary>Best-effort web content type for a served/stored static file.</summary>
    public static string ContentTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript",
        ".css" => "text/css",
        ".json" => "application/json",
        ".map" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".eot" => "application/vnd.ms-fontobject",
        ".txt" => "text/plain; charset=utf-8",
        ".xml" => "application/xml",
        ".webmanifest" => "application/manifest+json",
        ".pdf" => "application/pdf",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wasm" => "application/wasm",
        _ => "application/octet-stream",
    };
}
