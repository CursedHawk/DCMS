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
/// <param name="Publishing">What the last publish started (its commit, or its build's id): <c>builds --watch</c> waits for it.</param>
internal sealed record SyncState(string Server, string Tenant, string? Environment, string Workspace, string Name, string Branch,
    string? BaseSha, Dictionary<string, string> Hashes, string[]? Conflicts = null, string? Publishing = null);

internal static class Sync
{
    public const string StateFile = ".dcms/sync.json";

    /// <summary>
    /// Hidden files and folders (<c>.env</c>, <c>.npmrc</c>, <c>.git</c>, this tool's own <c>.dcms</c>)
    /// never leave this machine: what a folder holds besides a site's files is often a secret. Ones the
    /// server holds (a <c>.gitignore</c>) are still pulled, and stay the server's.
    /// </summary>
    public static bool IsHidden(string path) => path.Split('/').Any(p => p.StartsWith('.'));

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
                if (entry.LinkTarget is not null || entry.Name.StartsWith('.')) continue;
                var path = prefix + entry.Name;
                if (entry is DirectoryInfo sub)
                {
                    if (entry.Name is "node_modules") continue;
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
        var deleted = baseHashes.Keys.Where(p => !local.ContainsKey(p) && !IsHidden(p)).Order(StringComparer.Ordinal).ToList();
        return (added, modified, deleted);
    }

    public const string Ours = "<<<<<<< yours";
    public const string Split = "=======";
    public const string Theirs = ">>>>>>> server";

    /// <summary>
    /// Both sides of a conflicted text file, marked the way git marks them: the lines both share
    /// once, markers only around the runs that differ.
    /// </summary>
    public static string Marked(string? yours, string? theirs)
    {
        var a = Lines(yours);
        var b = Lines(theirs);
        var head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[^(tail + 1)] == b[^(tail + 1)]) tail++;
        var (ma, mb) = (a[head..^tail], b[head..^tail]);

        var text = new StringBuilder();
        foreach (var line in a[..head]) text.Append(line);
        var ours = new List<string>();
        var others = new List<string>();
        // ponytail: an LCS table over what differs, up to 4M cells (16 MB); past that, one block.
        if ((long)ma.Length * mb.Length <= 4_000_000)
        {
            var lcs = new int[ma.Length + 1, mb.Length + 1];
            for (var i = ma.Length - 1; i >= 0; i--)
                for (var j = mb.Length - 1; j >= 0; j--)
                    lcs[i, j] = ma[i] == mb[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < ma.Length && y < mb.Length)
            {
                if (ma[x] == mb[y])
                {
                    Block();
                    text.Append(ma[x++]);
                    y++;
                }
                else if (lcs[x + 1, y] >= lcs[x, y + 1]) ours.Add(ma[x++]);
                else others.Add(mb[y++]);
            }
            ours.AddRange(ma[x..]);
            others.AddRange(mb[y..]);
        }
        else
        {
            ours.AddRange(ma);
            others.AddRange(mb);
        }
        Block();
        foreach (var line in a[^tail..]) text.Append(line);
        return text.ToString();

        void Block()
        {
            if (ours.Count + others.Count == 0) return;
            text.Append(Ours).Append('\n');
            foreach (var line in ours) text.Append(line.EndsWith('\n') ? line : line + "\n");
            text.Append(Split).Append('\n');
            foreach (var line in others) text.Append(line.EndsWith('\n') ? line : line + "\n");
            text.Append(Theirs).Append('\n');
            ours.Clear();
            others.Clear();
        }
    }

    /// <summary>A text's lines, each with its own line ending (the last may have none).</summary>
    private static string[] Lines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var parts = text.Split('\n');
        var lines = parts[..^1].Select(l => l + "\n").ToList();
        if (parts[^1].Length > 0) lines.Add(parts[^1]);
        return [.. lines];
    }

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
            var theirs = server.GetValueOrDefault(path);
            if (IsHidden(path))
            {
                // Never read from here, so always the server's.
                write[path] = theirs;
                continue;
            }
            var mine = local.GetValueOrDefault(path);
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
