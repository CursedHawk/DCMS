using System.Text;
using System.Text.Json;

namespace Dcms.Shared.Data.Sites;

/// <summary>
/// Helpers for a site definition's file map (<c>{ "files": { path: content } }</c>).
/// Both git-backed render modes store their source this way — Mode B a React
/// project, Mode A the builder's <c>site.json</c> + <c>pages/*.html</c> +
/// <c>styles/*.css</c> — so the endpoints, the revision store and the builders all
/// share one parse/serialize/hash implementation and change detection stays
/// consistent between them.
/// </summary>
public static class SiteFileMap
{
    /// <summary>Parse a definition's file map (tolerant of a definition that has none).</summary>
    public static Dictionary<string, string> Parse(string definitionJson)
    {
        using var doc = JsonDocument.Parse(definitionJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Object)
        {
            return [];
        }
        return files.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
    }

    /// <summary>Serialize a file map back to the definition shape.</summary>
    public static string Serialize(IReadOnlyDictionary<string, string> files) =>
        JsonSerializer.Serialize(new { files });

    /// <summary>Hash every file's content (path → hash).</summary>
    public static Dictionary<string, string> HashAll(IReadOnlyDictionary<string, string> files) =>
        files.ToDictionary(kv => kv.Key, kv => Hash(kv.Value));

    /// <summary>
    /// Deterministic content hash (FNV-1a 64-bit, hex) shared with the IDE's change
    /// detection. Not cryptographic — it only needs to change when the content does.
    /// </summary>
    public static string Hash(string content)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(content))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash.ToString("x16");
    }

    public static bool IsSafePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.Contains("..", StringComparison.Ordinal) &&
        !path.StartsWith('/') &&
        !Path.IsPathRooted(path);

    private static readonly HashSet<string> Toolchain =
        new(StringComparer.OrdinalIgnoreCase) { "package.json", "pnpm-lock.yaml" };

    public static bool IsToolchainFile(string path) =>
        !path.Contains('/') && Toolchain.Contains(path);

    /// <summary>
    /// Extensions whose file-map value is base64-encoded raw bytes rather than
    /// text. Must match the admin SPA (site-source/binary.ts BINARY_EXTENSIONS)
    /// and the preview bundler so an upload round-trips losslessly. Everything
    /// else — including SVG — is literal UTF-8 text.
    /// </summary>
    public static readonly IReadOnlySet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".ico", ".bmp",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp3", ".mp4", ".webm", ".ogg", ".wav",
        ".pdf",
    };

    public static bool IsBinaryPath(string path) =>
        BinaryExtensions.Contains(Path.GetExtension(path));
}
