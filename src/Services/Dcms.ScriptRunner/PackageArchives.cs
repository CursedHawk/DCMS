using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Dcms.ScriptRunner;

/// <summary>
/// One package's archive in a build request (ADR 0031), base64: an npm tarball for
/// <c>node_modules/…</c>, a .nupkg for <c>nuget/{id}/{version}</c>.
/// </summary>
public sealed record PackageArchive(string Path, string Data)
{
    public bool IsNuGet => Path.StartsWith("nuget/", StringComparison.Ordinal);
}

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
                tgz = Convert.FromBase64String(archive.Data);
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

    // The best build for the sandbox's .NET 10, newest first; .NET Framework's never.
    private static readonly string[] Frameworks =
        ["net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netstandard2.1", "netstandard2.0",
         "netstandard1.6", "netstandard1.5", "netstandard1.4", "netstandard1.3", "netstandard1.2", "netstandard1.1", "netstandard1.0"];

    /// <summary>
    /// The assemblies each NuGet package gives a .NET 10 build: its best <c>lib/{framework}/</c>
    /// folder's DLLs. A package with only native code or only a .NET Framework build is refused;
    /// one with no library at all (analyzers, a meta-package) gives nothing.
    /// </summary>
    public static (List<(string Package, string Name, byte[] Bytes)> Assemblies, List<string> Problems) Libraries(
        IEnumerable<PackageArchive> archives, long maxBytes)
    {
        var assemblies = new List<(string, string, byte[])>();
        var problems = new List<string>();
        long total = 0;
        foreach (var archive in archives.Where(a => a.IsNuGet))
        {
            var package = archive.Path["nuget/".Length..].Replace('/', ' ');
            try
            {
                using var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String(archive.Data), writable: false), ZipArchiveMode.Read);
                var libs = zip.Entries
                    .Select(e => e.FullName.Replace('\\', '/'))
                    .Where(n => n.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) && n.Count(c => c == '/') == 2 && n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(n => n.Split('/')[1].ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.ToList());
                var best = Frameworks.FirstOrDefault(libs.ContainsKey);
                if (best is null)
                {
                    if (libs.Count > 0) problems.Add($"{package} has no build for .NET ({string.Join(", ", libs.Keys.Order())}).");
                    else if (zip.Entries.Any(e => e.FullName.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)))
                        problems.Add($"{package} is native code, which a function cannot run.");
                    continue;
                }
                foreach (var name in libs[best])
                {
                    var entry = zip.GetEntry(name) ?? zip.Entries.First(e => e.FullName.Replace('\\', '/') == name);
                    total += entry.Length;
                    if (total > maxBytes)
                    {
                        return ([], [$"The packages' assemblies are more than {maxBytes / (1024 * 1024)} MB."]);
                    }
                    using var stream = entry.Open();
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    var file = name.Split('/')[2];
                    if (assemblies.Any(a => string.Equals(a.Item2, file, StringComparison.OrdinalIgnoreCase)))
                    {
                        problems.Add($"{package} brings {file}, which another package brings too.");
                        continue;
                    }
                    assemblies.Add((package, file, copy.ToArray()));
                }
            }
            catch (Exception e) when (e is InvalidDataException or FormatException)
            {
                problems.Add($"{package}: the package is not a .nupkg.");
            }
        }
        return (assemblies, problems);
    }

    /// <summary>The package a bare specifier names: <c>zod</c>, <c>@scope/name</c>; null for anything else.</summary>
    public static string? PackageOf(string specifier)
    {
        if (specifier.Length == 0 || specifier[0] is '.' or '/' || specifier.Contains(':')) return null;
        var parts = specifier.Split('/');
        return specifier[0] == '@' ? parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : null : parts[0];
    }
}
