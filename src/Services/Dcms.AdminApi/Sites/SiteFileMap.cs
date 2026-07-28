using System.Text;
using System.Text.Json;

namespace Dcms.AdminApi.Sites;

/// <summary>
/// Helpers for the Mode B site definition's file map (<c>{ "files": { path: content } }</c>),
/// shared by the site endpoints and the revision store so change detection stays consistent.
/// </summary>
public static class SiteFileMap
{
    /// <summary>Parse a definition's file map (tolerant of a non-Mode-B definition).</summary>
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
}
