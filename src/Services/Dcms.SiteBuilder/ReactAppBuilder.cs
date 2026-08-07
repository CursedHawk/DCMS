using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Dcms.SiteBuilder;

public sealed record BuiltArtifact(string RelativePath, string LocalPath, string ContentType);

/// <summary>
/// Mode B (generated React app): the site definition is a file map
/// ({ "files": { path: content } }) that is a complete, self-contained front-end
/// project — including its own package.json and lockfile. The files are
/// materialized into a temp dir and built with the site's own package manager
/// (npm / pnpm / yarn, auto-detected from the lockfile), then `vite build`
/// (or the project's own build script). Install runs with network access from a
/// per-build, non-root, resource-limited work directory; the build itself already
/// executes site-supplied code (vite.config), so install lifecycle scripts are in
/// the same trust boundary. Returns the built static artifacts under dist/.
/// </summary>
public sealed class ReactAppBuilder(ILogger<ReactAppBuilder> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Install can be slow on a cold cache (fetching from the registry); the build
    // itself should be quick. Both are hard-capped so a hung step can't wedge the
    // builder (a stuck build is also reaped server-side).
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(8);

    /// <summary>
    /// Extensions whose file-map value is base64-encoded raw bytes rather than
    /// text. Must match the admin IDE (binary.ts BINARY_EXTENSIONS) and the
    /// preview bundler so an upload round-trips losslessly. Everything else —
    /// including SVG — is written as literal UTF-8 text.
    /// </summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".ico", ".bmp",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp3", ".mp4", ".webm", ".ogg", ".wav",
        ".pdf",
    };

    private static bool IsBinaryPath(string path) => BinaryExtensions.Contains(Path.GetExtension(path));

    /// <summary>Optional shared package-manager cache (warms repeat installs). Read-write
    /// is fine — it only holds downloaded tarballs, never a site's node_modules.</summary>
    private static string? CacheDir => Environment.GetEnvironmentVariable("DCMS_BUILD_CACHE_DIR");

    /// <summary>
    /// Build the site. All step output (install + build) is appended to
    /// <paramref name="log"/> so the caller can persist it whether the build
    /// succeeds or fails — a failing build must be inspectable in the IDE.
    /// </summary>
    public async Task<IReadOnlyList<BuiltArtifact>> BuildAsync(
        string definitionSnapshotJson, string workDir, StringBuilder log, CancellationToken ct)
    {
        var files = ParseFileMap(definitionSnapshotJson);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("This site has no files to build. Add your React app source in the editor, then publish.");
        }

        foreach (var (relative, content) in files)
        {
            if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                throw new InvalidOperationException($"Illegal path in file map: {relative}");
            }
            var target = Path.Combine(workDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (IsBinaryPath(relative))
            {
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(content);
                }
                catch (FormatException)
                {
                    throw new InvalidOperationException(
                        $"Binary file '{relative}' is not valid base64. Re-upload it in the site editor.");
                }
                await File.WriteAllBytesAsync(target, bytes, ct);
            }
            else
            {
                await File.WriteAllTextAsync(target, content, ct);
            }
        }

        if (!File.Exists(Path.Combine(workDir, "package.json")))
        {
            // Legacy site with no package.json (older sites relied on a platform-provided
            // toolchain). Synthesize a sensible default so it still builds; the site can
            // add its own package.json in the editor to control its dependencies.
            log.AppendLine("› No package.json found — using a default React + Vite project.");
            await File.WriteAllTextAsync(Path.Combine(workDir, "package.json"), DefaultPackageJson, ct);
        }

        var pm = PackageManager.Detect(workDir);
        log.AppendLine($"› Detected {pm.Name} project.");

        try
        {
            await RunAsync(pm.Exe, pm.InstallArgs, workDir, "install dependencies", InstallTimeout, log, ct);
        }
        catch (InvalidOperationException) when (pm.InstallFallbackArgs is { } fallback)
        {
            // A strict install (frozen lockfile / npm ci) can fail when the lockfile is
            // out of sync with package.json. Retry with a lenient install that updates it.
            log.AppendLine("› Strict install failed — retrying with a lenient install.");
            await RunAsync(pm.Exe, fallback, workDir, "install dependencies", InstallTimeout, log, ct);
        }
        await RunAsync(pm.Exe, pm.BuildArgs(workDir), workDir, "build", BuildTimeout, log, ct);

        var dist = Path.Combine(workDir, "dist");
        if (!Directory.Exists(dist))
        {
            var produced = Directory.Exists(workDir)
                ? string.Join(", ", Directory.EnumerateDirectories(workDir).Select(Path.GetFileName).Where(n => n != "node_modules"))
                : "";
            throw new InvalidOperationException(
                $"The build did not produce a dist/ directory. Vite outputs dist/ by default — check your build script and vite.config. Top-level folders produced: {(produced.Length == 0 ? "(none)" : produced)}.");
        }

        return Directory.EnumerateFiles(dist, "*", SearchOption.AllDirectories)
            .Select(p => new BuiltArtifact(
                Path.GetRelativePath(dist, p).Replace(Path.DirectorySeparatorChar, '/'),
                p,
                ContentTypeFor(p)))
            .ToList();
    }

    // Fallback project for a site that shipped no package.json. No lockfile → the
    // package manager resolves current matching versions from the registry.
    private const string DefaultPackageJson = """
        {
          "name": "site",
          "private": true,
          "type": "module",
          "scripts": { "build": "vite build" },
          "dependencies": {
            "react": "^19",
            "react-dom": "^19",
            "react-router-dom": "^6"
          },
          "devDependencies": {
            "@vitejs/plugin-react": "^4",
            "vite": "^6"
          }
        }
        """;

    private static Dictionary<string, string> ParseFileMap(string snapshotJson)
    {
        using var doc = JsonDocument.Parse(snapshotJson);
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
        {
            return [];
        }
        return files.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
    }

    /// <summary>
    /// Resolves which package manager to drive from the committed lockfile and
    /// whether the project defines its own build script.
    /// </summary>
    private sealed record PackageManager(
        string Name, string Exe, string InstallArgs, string? InstallFallbackArgs, Func<string, string> BuildArgs)
    {
        public static PackageManager Detect(string workDir)
        {
            bool Has(string f) => File.Exists(Path.Combine(workDir, f));
            var hasBuildScript = HasBuildScript(workDir);

            if (Has("pnpm-lock.yaml"))
            {
                return new PackageManager("pnpm", "pnpm",
                    "install --frozen-lockfile", "install --no-frozen-lockfile",
                    _ => hasBuildScript ? "run build" : "exec vite build");
            }
            if (Has("yarn.lock"))
            {
                return new PackageManager("yarn", "yarn",
                    "install --frozen-lockfile", "install",
                    _ => hasBuildScript ? "run build" : "exec vite build");
            }
            // Default to npm. `npm ci` requires a lockfile in sync; fall back to install.
            var hasLock = Has("package-lock.json");
            return new PackageManager("npm", "npm",
                hasLock ? "ci --no-audit --no-fund" : "install --no-audit --no-fund",
                hasLock ? "install --no-audit --no-fund" : null,
                _ => hasBuildScript ? "run build" : "exec -- vite build");
        }

        private static bool HasBuildScript(string workDir)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(workDir, "package.json")));
                return doc.RootElement.TryGetProperty("scripts", out var scripts) &&
                       scripts.ValueKind == JsonValueKind.Object &&
                       scripts.TryGetProperty("build", out var b) &&
                       b.ValueKind == JsonValueKind.String &&
                       !string.IsNullOrWhiteSpace(b.GetString());
            }
            catch
            {
                return false;
            }
        }
    }

    private async Task RunAsync(
        string fileName, string arguments, string workDir, string label,
        TimeSpan timeout, StringBuilder log, CancellationToken ct)
    {
        log.AppendLine($"$ {fileName} {arguments}");
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workDir,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            Environment =
            {
                ["CI"] = "1",
                // The container user does not own the image's HOME, and the package
                // managers write cache/state there. Keep that inside the (writable,
                // per-build, discarded) work directory.
                ["HOME"] = workDir,
                ["npm_config_audit"] = "false",
                ["npm_config_fund"] = "false",
                ["npm_config_update_notifier"] = "false",
                ["ADBLOCK"] = "1",
                ["DISABLE_OPENCOLLECTIVE"] = "1",
            },
        };
        var cache = CacheDir;
        if (!string.IsNullOrEmpty(cache))
        {
            startInfo.Environment["npm_config_cache"] = cache;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Drain both pipes while the process runs — package managers report failures
        // on stdout, and letting either buffer fill would deadlock the build.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            var partial = string.Join('\n', (await stderr).Trim(), (await stdout).Trim()).Trim();
            log.AppendLine(partial);
            log.AppendLine($"✗ Timed out after {timeout.TotalMinutes:0} min during {label}.");
            throw new InvalidOperationException($"Timed out after {timeout.TotalMinutes:0} minutes while trying to {label}.");
        }

        var output = string.Join('\n', (await stdout).Trim(), (await stderr).Trim()).Trim();
        if (output.Length > 0) log.AppendLine(output);

        if (process.ExitCode != 0)
        {
            logger.LogWarning("Build step `{File} {Args}` failed: {Output}", fileName, arguments, output);
            var tail = Tail(output, 1500);
            throw new InvalidOperationException($"Failed to {label}.\n{tail}");
        }
    }

    private static string Tail(string s, int max) =>
        s.Length <= max ? s : "…" + s[^max..];

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript",
        ".css" => "text/css",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".map" => "application/json",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };
}
