using System.Text;

namespace Dcms.Cli;

/// <summary>
/// A site's or app's files in a folder on this machine (ADR 0032, C5), kept in step with the
/// person's draft on the server by hashes: the server's FNV-1a over each file's text, as
/// <c>SiteFileMap.Hash</c> computes it. Binary files are base64 text on the server (and in git):
/// real bytes on disk.
/// </summary>
/// <param name="Workspace">Where its routes are: <c>sites/{id}</c> or <c>repos/{bindingId}</c>.</param>
/// <param name="Hashes">The server's hash of every file as last pulled or pushed: the base a change is measured against.</param>
/// <param name="Conflicts">Files holding conflict markers from a commit, to send as resolutions once edited.</param>
internal sealed record SyncState(string Server, string Tenant, string? Environment, string Workspace, string Name, string Branch,
    string? BaseSha, Dictionary<string, string> Hashes, string[]? Conflicts = null);

internal static class Sync
{
    public const string StateFile = ".dcms/sync.json";

    /// <summary>What a folder never sends: the state, git's and package managers' own folders.</summary>
    private static readonly string[] Skipped = [".dcms", ".git", "node_modules"];

    /// <summary>The server's hash of a file's text (<c>SiteFileMap.Hash</c>): FNV-1a 64 over its UTF-8, lowercase hex.</summary>
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

    /// <summary>Files the server keeps as base64 text (<c>SiteFileMap.BinaryExtensions</c>; a test keeps the two the same).</summary>
    public static readonly IReadOnlySet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".ico", ".bmp",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp3", ".mp4", ".webm", ".ogg", ".wav",
        ".pdf",
    };

    public static bool IsBinary(string path) => BinaryExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// A file path as a site's draft may name one: relative, forward slashes, no empty, <c>.</c> or
    /// <c>..</c> segment. Whatever the server sends is held to it before it touches this machine.
    /// </summary>
    public static bool IsSafePath(string path) =>
        path.Length > 0 && !path.Contains('\\') && !path.Contains(':') && !Path.IsPathRooted(path)
        && path.Split('/').All(p => p.Length > 0 && p != "." && p != "..");

    /// <summary>A file's bytes as the server holds them.</summary>
    public static string ToServer(string path, byte[] bytes) => IsBinary(path) ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes);

    /// <summary>A file as the server holds it, as bytes for the disk.</summary>
    public static byte[] ToDisk(string path, string content)
    {
        if (!IsBinary(path)) return Encoding.UTF8.GetBytes(content);
        try
        {
            return Convert.FromBase64String(content);
        }
        catch (FormatException)
        {
            // Not base64 after all (an older file): as it is.
            return Encoding.UTF8.GetBytes(content);
        }
    }

    /// <summary>The folder's files as the server would hold them, never through a symbolic link.</summary>
    public static Dictionary<string, string> LocalFiles(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(root, "");
        return files;

        void Walk(string dir, string prefix)
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null) continue;
                var path = prefix + entry.Name;
                if (entry is DirectoryInfo sub)
                {
                    if (prefix.Length == 0 && Skipped.Contains(entry.Name, StringComparer.Ordinal)) continue;
                    if (entry.Name is ".git" or "node_modules") continue;
                    Walk(sub.FullName, path + "/");
                }
                else
                {
                    files[path] = ToServer(path, File.ReadAllBytes(entry.FullName));
                }
            }
        }
    }

    /// <summary>What changed on disk since the base: new, changed and deleted files.</summary>
    public static (List<string> Added, List<string> Modified, List<string> Deleted) Changes(
        IReadOnlyDictionary<string, string> local, IReadOnlyDictionary<string, string> baseHashes)
    {
        var added = local.Keys.Where(p => !baseHashes.ContainsKey(p)).Order(StringComparer.Ordinal).ToList();
        var modified = local.Where(f => baseHashes.TryGetValue(f.Key, out var h) && h != Hash(f.Value)).Select(f => f.Key).Order(StringComparer.Ordinal).ToList();
        var deleted = baseHashes.Keys.Where(p => !local.ContainsKey(p)).Order(StringComparer.Ordinal).ToList();
        return (added, modified, deleted);
    }

    public const string Ours = "<<<<<<< yours";
    public const string Split = "=======";
    public const string Theirs = ">>>>>>> server";

    /// <summary>Both sides of a conflicted text file, marked the way git marks them.</summary>
    public static string Marked(string? yours, string? theirs) =>
        $"{Ours}\n{yours ?? ""}{(yours is { Length: > 0 } && !yours.EndsWith('\n') ? "\n" : "")}{Split}\n{theirs ?? ""}"
        + $"{(theirs is { Length: > 0 } && !theirs.EndsWith('\n') ? "\n" : "")}{Theirs}\n";

    public static bool HasMarkers(string text) =>
        text.Split('\n').Any(l => l.StartsWith("<<<<<<< ", StringComparison.Ordinal) || l == Split || l.StartsWith(">>>>>>> ", StringComparison.Ordinal));

    /// <summary>
    /// A pull onto a folder that has changes of its own: each file goes the way that loses nothing.
    /// Unchanged here: the server's. Unchanged there: ours. Changed on both sides: markers (or ours, for a binary file).
    /// </summary>
    public static (Dictionary<string, string?> Write, List<string> Conflicts) Merge(
        IReadOnlyDictionary<string, string> local, IReadOnlyDictionary<string, string> baseHashes, IReadOnlyDictionary<string, string> server)
    {
        var write = new Dictionary<string, string?>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        foreach (var path in local.Keys.Union(server.Keys).Union(baseHashes.Keys).Distinct(StringComparer.Ordinal))
        {
            var mine = local.GetValueOrDefault(path);
            var theirs = server.GetValueOrDefault(path);
            var basis = baseHashes.GetValueOrDefault(path);
            var mineHash = mine is null ? null : Hash(mine);
            var theirsHash = theirs is null ? null : Hash(theirs);
            if (mineHash == theirsHash || theirsHash == basis) continue; // the same, or nothing new there: keep ours
            if (mineHash == basis)
            {
                write[path] = theirs; // nothing new here: theirs (null deletes)
                continue;
            }
            conflicts.Add(path);
            if (!IsBinary(path)) write[path] = Marked(mine, theirs);
        }
        return (write, conflicts);
    }
}
