using System.Diagnostics;
using System.Text.Json;

namespace Dcms.SiteBuilder;

public sealed record BuiltArtifact(string RelativePath, string LocalPath, string ContentType);

/// <summary>
/// Mode B (generated React app): the site definition is a whitelisted file map
/// ({ "files": { path: content } }). The files are materialized into a temp dir
/// and built with a sandboxed, offline pnpm + vite build inside the site-builder
/// container (which carries Node + pnpm). The build runs with no network and a
/// time limit; the AI cannot add dependencies (the lockfile is pinned in the
/// template). Returns the built static artifacts under dist/.
/// </summary>
public sealed class ReactAppBuilder(ILogger<ReactAppBuilder> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Files the platform owns. A site's file map may not supply these — the
    /// dependency set is fixed by the image so an offline install always resolves.
    /// </summary>
    private static readonly string[] ToolchainFiles = ["package.json", "pnpm-lock.yaml"];

    private static string ToolchainDir =>
        Environment.GetEnvironmentVariable("DCMS_TOOLCHAIN_DIR") ?? "/opt/dcms/toolchain";

    private static string? StoreDir =>
        Environment.GetEnvironmentVariable("DCMS_PNPM_STORE_DIR");

    public async Task<IReadOnlyList<BuiltArtifact>> BuildAsync(string definitionSnapshotJson, string workDir, CancellationToken ct)
    {
        var files = ParseFileMap(definitionSnapshotJson);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("React app definition contains no files.");
        }

        foreach (var (relative, content) in files)
        {
            if (relative.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Illegal path in file map: {relative}");
            }
            if (ToolchainFiles.Contains(Path.GetFileName(relative), StringComparer.OrdinalIgnoreCase) &&
                !relative.Contains('/'))
            {
                throw new InvalidOperationException(
                    $"'{relative}' is provided by the platform toolchain and may not be set by a site definition.");
            }
            var target = Path.Combine(workDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, content, ct);
        }

        CopyToolchain(workDir);

        // --store-dir must be passed explicitly: pnpm does not honour the
        // npm_config_store_dir environment variable, so without the flag it
        // falls back to a per-HOME default and the warmed store is not found.
        var store = PrepareStore(workDir);
        var storeArg = string.IsNullOrEmpty(store) ? string.Empty : $" --store-dir {store}";
        await RunAsync("pnpm", $"install --offline --frozen-lockfile --ignore-scripts{storeArg}", workDir, ct);
        await RunAsync("pnpm", "exec vite build", workDir, ct);

        var dist = Path.Combine(workDir, "dist");
        if (!Directory.Exists(dist))
        {
            throw new InvalidOperationException("Build did not produce a dist/ directory.");
        }

        return Directory.EnumerateFiles(dist, "*", SearchOption.AllDirectories)
            .Select(p => new BuiltArtifact(
                Path.GetRelativePath(dist, p).Replace(Path.DirectorySeparatorChar, '/'),
                p,
                ContentTypeFor(p)))
            .ToList();
    }

    /// <summary>
    /// Places the image's pinned package.json + lockfile into the build directory.
    /// This is what makes `--offline --frozen-lockfile` resolvable: the lockfile
    /// matches the store that was warmed at image build.
    /// </summary>
    private static void CopyToolchain(string workDir)
    {
        foreach (var file in ToolchainFiles)
        {
            var source = Path.Combine(ToolchainDir, file);
            if (!File.Exists(source))
            {
                throw new InvalidOperationException(
                    $"Site builder toolchain file '{file}' is missing from {ToolchainDir}. " +
                    "The image was built without a warmed offline pnpm store.");
            }
            File.Copy(source, Path.Combine(workDir, file), overwrite: true);
        }
    }

    /// <summary>
    /// Builds a per-build pnpm store that reuses the image's warmed content.
    ///
    /// The warmed store is owned by root and read-only to the build user, but
    /// pnpm writes its SQLite index and project metadata even for an offline
    /// install. Making the shared store writable instead would let one site's
    /// build corrupt the dependencies served to every other tenant — a site
    /// supplies its own vite.config.ts, which executes during `vite build`.
    ///
    /// So the large content-addressed `files` tree is shared through a symlink
    /// (read-only in practice, since --offline never adds to it) while the index
    /// and project metadata are per-build copies. The store lives inside the
    /// work directory and is discarded with it; Directory.Delete does not
    /// recurse through the symlink.
    /// </summary>
    private static string PrepareStore(string workDir)
    {
        if (string.IsNullOrEmpty(StoreDir) || !Directory.Exists(StoreDir))
        {
            return string.Empty;
        }

        // pnpm appends its own store-version directory (e.g. "v11") to --store-dir.
        var source = Directory.EnumerateDirectories(StoreDir).SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"Expected exactly one versioned store directory under {StoreDir}.");

        var root = Path.Combine(workDir, ".pnpm-store");
        var target = Path.Combine(root, Path.GetFileName(source));
        Directory.CreateDirectory(target);

        Directory.CreateSymbolicLink(Path.Combine(target, "files"), Path.Combine(source, "files"));

        var index = Path.Combine(source, "index.db");
        if (File.Exists(index))
        {
            File.Copy(index, Path.Combine(target, "index.db"));
        }
        Directory.CreateDirectory(Path.Combine(target, "projects"));

        return root;
    }

    private static Dictionary<string, string> ParseFileMap(string snapshotJson)
    {
        using var doc = JsonDocument.Parse(snapshotJson);
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
        {
            return [];
        }
        return files.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
    }

    private async Task RunAsync(string fileName, string arguments, string workDir, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = workDir,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                // No network: the offline pnpm store is pre-warmed in the image.
                Environment =
                {
                    ["CI"] = "1",
                    ["npm_config_offline"] = "true",
                    // The container user does not own the image's HOME, and pnpm
                    // writes its cache/state there. Keep that inside the (writable,
                    // per-build, discarded) work directory.
                    ["HOME"] = workDir,
                },
            },
        };
        process.Start();

        // Drain both pipes while the process runs — pnpm reports failures on
        // stdout, and letting either buffer fill would deadlock the build.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
        {
            var output = string.Join('\n', (await stderr).Trim(), (await stdout).Trim()).Trim();
            logger.LogWarning("Build step `{File} {Args}` failed: {Output}", fileName, arguments, output);
            throw new InvalidOperationException($"Build step failed: {fileName} {arguments}\n{output}");
        }
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript",
        ".css" => "text/css",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };
}
