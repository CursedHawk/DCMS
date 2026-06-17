using System.IO.Compression;
using Dcms.Shared.Storage;

namespace Dcms.AdminApi.Sites;

/// <summary>Raised when an uploaded static bundle violates the size/shape guard-rails.</summary>
public sealed class StaticBundleException(string message) : Exception(message);

/// <summary>
/// Turns a multipart upload (a single .zip, a set of loose files, or a folder
/// upload carrying relative paths) into one sanitized, normalized bundle zip
/// ready to stage in object storage. Rejects traversal/zip-slip, enforces size
/// and file-count limits, and collapses a single wrapper folder so the web root
/// (the directory containing index.html) sits at the top.
/// </summary>
public static class StaticBundleBuilder
{
    public sealed record Result(byte[] ZipBytes, int FileCount, long TotalBytes, string? RootName, bool HasIndex);

    public static async Task<Result> BuildAsync(IFormFileCollection files, CancellationToken ct)
    {
        // Collected relative path → content. Case-insensitive to dedupe path-casing.
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        void Add(string? rawPath, byte[] bytes)
        {
            var path = StaticSiteFiles.NormalizeEntryPath(rawPath);
            if (path is null)
            {
                return;
            }
            if (bytes.LongLength > StaticSiteFiles.MaxFileBytes)
            {
                throw new StaticBundleException($"File '{path}' exceeds the {StaticSiteFiles.MaxFileBytes / (1024 * 1024)} MB per-file limit.");
            }
            total += bytes.LongLength;
            if (total > StaticSiteFiles.MaxUncompressedBytes)
            {
                throw new StaticBundleException("The uploaded files exceed the total size limit.");
            }
            map[path] = bytes;
            if (map.Count > StaticSiteFiles.MaxFileCount)
            {
                throw new StaticBundleException($"The bundle exceeds the {StaticSiteFiles.MaxFileCount} file limit.");
            }
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Length == 0)
            {
                continue;
            }

            if (IsZip(file))
            {
                // ZipArchive needs a seekable stream.
                await using var raw = file.OpenReadStream();
                using var seekable = new MemoryStream();
                await raw.CopyToAsync(seekable, ct);
                seekable.Position = 0;
                using var archive = new ZipArchive(seekable, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/'))
                    {
                        continue; // directory
                    }
                    if (entry.Length > StaticSiteFiles.MaxFileBytes)
                    {
                        throw new StaticBundleException($"File '{entry.FullName}' exceeds the per-file limit.");
                    }
                    await using var es = entry.Open();
                    using var buffer = new MemoryStream();
                    await es.CopyToAsync(buffer, ct);
                    Add(entry.FullName, buffer.ToArray());
                }
            }
            else
            {
                await using var rs = file.OpenReadStream();
                using var buffer = new MemoryStream();
                await rs.CopyToAsync(buffer, ct);
                Add(file.FileName, buffer.ToArray());
            }
        }

        if (map.Count == 0)
        {
            throw new StaticBundleException("No usable files were found in the upload.");
        }

        var (normalized, rootName) = NormalizeWebRoot(map);
        var hasIndex = normalized.ContainsKey("index.html");

        var zipBytes = WriteZip(normalized);
        return new Result(zipBytes, normalized.Count, total, rootName, hasIndex);
    }

    private static bool IsZip(IFormFile file)
        => file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
           || string.Equals(file.ContentType, "application/zip", StringComparison.OrdinalIgnoreCase)
           || string.Equals(file.ContentType, "application/x-zip-compressed", StringComparison.OrdinalIgnoreCase);

    // If every file lives under a single wrapper directory (e.g. "dist/") or the
    // shallowest index.html is nested, treat the directory holding the shallowest
    // index.html as the web root and strip its prefix. Files outside that root are
    // dropped. When no index.html exists the map is returned unchanged.
    private static (Dictionary<string, byte[]> Map, string? RootName) NormalizeWebRoot(Dictionary<string, byte[]> map)
    {
        string? indexKey = null;
        var bestDepth = int.MaxValue;
        foreach (var key in map.Keys)
        {
            if (key.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
            {
                var depth = key.Count(c => c == '/');
                if (depth < bestDepth)
                {
                    bestDepth = depth;
                    indexKey = key;
                }
            }
        }

        if (indexKey is null)
        {
            return (map, null);
        }

        var slash = indexKey.LastIndexOf('/');
        if (slash < 0)
        {
            return (map, null); // index.html already at root
        }

        var prefix = indexKey[..(slash + 1)]; // includes trailing slash
        var rebased = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, bytes) in map)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                rebased[key[prefix.Length..]] = bytes;
            }
        }
        var rootName = prefix.TrimEnd('/');
        var lastSlash = rootName.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            rootName = rootName[(lastSlash + 1)..];
        }
        return (rebased, rootName);
    }

    private static byte[] WriteZip(Dictionary<string, byte[]> map)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in map)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var es = entry.Open();
                es.Write(bytes, 0, bytes.Length);
            }
        }
        return output.ToArray();
    }
}
