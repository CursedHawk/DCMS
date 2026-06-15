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
            var target = Path.Combine(workDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, content, ct);
        }

        await RunAsync("pnpm", "install --offline --frozen-lockfile", workDir, ct);
        await RunAsync("pnpm", "build", workDir, ct);

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
                Environment = { ["CI"] = "1", ["npm_config_offline"] = "true" },
            },
        };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync(ct);
            logger.LogWarning("Build step `{File} {Args}` failed: {Err}", fileName, arguments, err);
            throw new InvalidOperationException($"Build step failed: {fileName} {arguments}");
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
