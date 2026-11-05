using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Dcms.ScriptRunner;

/// <summary>One package's archive in a build request: where it goes, and the npm tarball, base64.</summary>
public sealed record PackageArchive(string Path, string Tgz);

/// <summary>
/// Unpacks a build's packages (ADR 0031) into its directory, offline. admin-api fetched them and
/// checked their integrity; here they are only extracted: regular files and folders only, never a
/// link, never a path that leaves its package's folder, within a size and file budget. Nothing in
/// them runs: no install script, and the build tools read them as code to check and bundle.
/// </summary>
public static partial class PackageArchives
{
    [GeneratedRegex(@"^node_modules/(?:@[a-z0-9-~][a-z0-9-._~]*/)?[a-z0-9-~][a-z0-9-._~]*(?:/node_modules/(?:@[a-z0-9-~][a-z0-9-._~]*/)?[a-z0-9-~][a-z0-9-._~]*)*$")]
    private static partial Regex InstallPath();

    /// <summary>Null when every archive unpacked; else what was wrong, for the build to report.</summary>
    public static async Task<string?> UnpackAsync(string root, IReadOnlyList<PackageArchive> archives, long maxBytes, int maxFiles, CancellationToken ct)
    {
        long bytes = 0;
        var files = 0;
        foreach (var archive in archives)
        {
            if (!InstallPath().IsMatch(archive.Path))
            {
                return $"'{archive.Path}' is not a place a package is installed.";
            }
            var target = Path.GetFullPath(Path.Combine(root, archive.Path));
            byte[] tgz;
            try
            {
                tgz = Convert.FromBase64String(archive.Tgz);
            }
            catch (FormatException)
            {
                return $"{archive.Path}: the archive is not base64.";
            }
            try
            {
                await using var gzip = new GZipStream(new MemoryStream(tgz, writable: false), CompressionMode.Decompress);
                await using var tar = new TarReader(gzip);
                while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                    {
                        // Links (and devices, fifos) are left out: a link could point anywhere.
                        continue;
                    }
                    // npm packs everything under one top folder ("package/" by convention): drop it.
                    var name = entry.Name.Replace('\\', '/');
                    var slash = name.IndexOf('/');
                    if (slash < 0) continue;
                    var relative = name[(slash + 1)..];
                    if (relative.Length == 0) continue;
                    var full = Path.GetFullPath(Path.Combine(target, relative));
                    if (!full.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        return $"{archive.Path}: '{entry.Name}' would land outside the package.";
                    }
                    if (entry.EntryType == TarEntryType.Directory)
                    {
                        Directory.CreateDirectory(full);
                        continue;
                    }
                    bytes += entry.Length;
                    if (++files > maxFiles || bytes > maxBytes)
                    {
                        return $"The packages unpack to more than {maxFiles:N0} files or {maxBytes / (1024 * 1024)} MB.";
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    if (entry.DataStream is { } data)
                    {
                        await using var output = File.Create(full);
                        await data.CopyToAsync(output, ct);
                    }
                    else
                    {
                        await File.WriteAllBytesAsync(full, [], ct);
                    }
                }
            }
            catch (Exception e) when (e is InvalidDataException or FormatException or EndOfStreamException)
            {
                return $"{archive.Path}: the archive is not a gzipped tarball.";
            }
        }
        return null;
    }

    /// <summary>The package a bare specifier names: <c>zod</c>, <c>@scope/name</c>; null for anything else.</summary>
    public static string? PackageOf(string specifier)
    {
        if (specifier.Length == 0 || specifier[0] is '.' or '/' || specifier.Contains(':')) return null;
        var parts = specifier.Split('/');
        return specifier[0] == '@' ? parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : null : parts[0];
    }
}
