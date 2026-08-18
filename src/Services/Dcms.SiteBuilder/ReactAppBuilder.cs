using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dcms.Shared.Data.Sites;

namespace Dcms.SiteBuilder;

public sealed record BuiltArtifact(string RelativePath, string LocalPath, string ContentType);

/// <summary>
/// Mode B (generated React app): the site definition is a file map
/// ({ "files": { path: content } }) that is a complete, self-contained front-end
/// project — including its own package.json and lockfile. Because the project is
/// untrusted (any tenant can push one), the build is treated as hostile code and
/// runs in an <b>isolated sandbox</b>, not in the builder service process:
///
/// <list type="bullet">
///   <item>The install and build run inside an ephemeral, per-build container
///     (see <see cref="SandboxOptions"/>) launched over a restricted Docker API
///     (docker-socket-proxy) — optionally on the gVisor runtime — with a
///     scrubbed environment (no platform secrets), dropped capabilities,
///     no-new-privileges, a read-only rootfs, and hard cpu/memory/pids limits.</item>
///   <item><b>Install</b> runs with <c>--ignore-scripts</c>, so no package
///     lifecycle script (pre/post-install, prepare) of the project or any
///     transitive dependency executes — the install phase runs zero untrusted
///     code, so it may keep network access to fetch dependencies.</item>
///   <item><b>Build</b> (<c>vite build</c>) does execute site-supplied code
///     (vite.config), so it runs with <b>no network at all</b> (<c>--network none</c>)
///     and the same scrubbed env — any code that runs cannot reach secrets,
///     other tenants, or the internet, so it is harmless.</item>
/// </list>
///
/// When no sandbox image is configured the builder falls back to running the
/// package manager directly (still with a scrubbed env and <c>--ignore-scripts</c>)
/// for local development; production sets <c>DCMS_BUILD_REQUIRE_SANDBOX=true</c>
/// so a missing sandbox fails the build instead of running untrusted code
/// unsandboxed. Returns the built static artifacts under dist/.
/// </summary>
public sealed class ReactAppBuilder(ILogger<ReactAppBuilder> logger)
{
    // Install can be slow on a cold cache (fetching from the registry); the build
    // itself should be quick. Both are hard-capped so a hung step can't wedge the
    // builder (a stuck build is also reaped server-side).
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(8);

    private readonly SandboxOptions _sandbox = SandboxOptions.FromEnvironment();

    private static bool IsBinaryPath(string path) => SiteFileMap.IsBinaryPath(path);

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

        // A build is untrusted code. Refuse to run it unsandboxed where the operator
        // has demanded a sandbox (production), rather than silently exposing the host.
        if (_sandbox.Required && !_sandbox.Enabled)
        {
            throw new InvalidOperationException(
                "The build sandbox is not configured but DCMS_BUILD_REQUIRE_SANDBOX is set. " +
                "Set DCMS_BUILD_SANDBOX_IMAGE and DCMS_BUILD_WORK_HOST_ROOT so builds run isolated.");
        }
        if (_sandbox.Enabled)
        {
            log.AppendLine($"› Building in an isolated sandbox ({_sandbox.Image}{(_sandbox.Runtime is { } rt ? $", runtime={rt}" : "")}).");
        }
        else
        {
            log.AppendLine("› Building unsandboxed (development mode) — install scripts are still disabled.");
        }

        try
        {
            await RunPhaseAsync(BuildPhase.Install, pm.Exe, pm.InstallArgs, workDir,
                "install dependencies", InstallTimeout, log, ct);
        }
        catch (InvalidOperationException) when (pm.InstallFallbackArgs is { } fallback)
        {
            // A strict install (frozen lockfile / npm ci) can fail when the lockfile is
            // out of sync with package.json. Retry with a lenient install that updates it.
            log.AppendLine("› Strict install failed — retrying with a lenient install.");
            await RunPhaseAsync(BuildPhase.Install, pm.Exe, fallback, workDir,
                "install dependencies", InstallTimeout, log, ct);
        }
        await RunPhaseAsync(BuildPhase.Build, pm.Exe, pm.BuildArgs(workDir), workDir,
            "build", BuildTimeout, log, ct);

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
            .Where(p => !IsSymlink(p))
            .Select(p => new BuiltArtifact(
                Path.GetRelativePath(dist, p).Replace(Path.DirectorySeparatorChar, '/'),
                p,
                ContentTypeFor(p)))
            .ToList();
    }

    // Fallback project for a legacy site that shipped no package.json. Such sites were
    // authored against the platform's fixed dependency palette (before builds became
    // bring-your-own-deps), so the synthesized default MUST provide that whole palette
    // — otherwise their vite.config/source imports (e.g. @tailwindcss/vite, react-router,
    // lucide-react) fail to resolve. Keep this in sync with
    // packages/site-builder-toolchain/package.json (the same palette the IDE surfaces).
    private const string DefaultPackageJson = """
        {
          "name": "site",
          "private": true,
          "type": "module",
          "scripts": { "build": "vite build" },
          "dependencies": {
            "@emotion/react": "11.14.0",
            "@emotion/styled": "11.14.1",
            "@mui/icons-material": "7.3.5",
            "@mui/material": "7.3.5",
            "@popperjs/core": "2.11.8",
            "@radix-ui/react-accordion": "1.2.3",
            "@radix-ui/react-alert-dialog": "1.1.6",
            "@radix-ui/react-aspect-ratio": "1.1.2",
            "@radix-ui/react-avatar": "1.1.3",
            "@radix-ui/react-checkbox": "1.1.4",
            "@radix-ui/react-collapsible": "1.1.3",
            "@radix-ui/react-context-menu": "2.2.6",
            "@radix-ui/react-dialog": "1.1.6",
            "@radix-ui/react-dropdown-menu": "2.1.6",
            "@radix-ui/react-hover-card": "1.1.6",
            "@radix-ui/react-label": "2.1.2",
            "@radix-ui/react-menubar": "1.1.6",
            "@radix-ui/react-navigation-menu": "1.2.5",
            "@radix-ui/react-popover": "1.1.6",
            "@radix-ui/react-progress": "1.1.2",
            "@radix-ui/react-radio-group": "1.2.3",
            "@radix-ui/react-scroll-area": "1.2.3",
            "@radix-ui/react-select": "2.1.6",
            "@radix-ui/react-separator": "1.1.2",
            "@radix-ui/react-slider": "1.2.3",
            "@radix-ui/react-slot": "1.1.2",
            "@radix-ui/react-switch": "1.1.3",
            "@radix-ui/react-tabs": "1.1.3",
            "@radix-ui/react-toggle": "1.1.2",
            "@radix-ui/react-toggle-group": "1.1.2",
            "@radix-ui/react-tooltip": "1.1.8",
            "class-variance-authority": "0.7.1",
            "clsx": "2.1.1",
            "cmdk": "1.1.1",
            "date-fns": "3.6.0",
            "embla-carousel-react": "8.6.0",
            "input-otp": "1.4.2",
            "lucide-react": "0.487.0",
            "motion": "12.23.24",
            "next-themes": "0.4.6",
            "react": "19.2.7",
            "react-day-picker": "8.10.1",
            "react-dnd": "16.0.1",
            "react-dnd-html5-backend": "16.0.1",
            "react-dom": "19.2.7",
            "react-hook-form": "7.55.0",
            "react-popper": "2.3.0",
            "react-resizable-panels": "2.1.7",
            "react-responsive-masonry": "2.7.1",
            "react-router": "7.13.0",
            "react-slick": "0.31.0",
            "recharts": "2.15.2",
            "slick-carousel": "1.8.1",
            "sonner": "2.0.3",
            "tailwind-merge": "3.2.0",
            "tw-animate-css": "1.3.8",
            "vaul": "1.1.2"
          },
          "devDependencies": {
            "@tailwindcss/vite": "4.1.12",
            "@types/react": "19.2.17",
            "@types/react-dom": "19.2.3",
            "@types/react-slick": "0.23.13",
            "@vitejs/plugin-react": "4.7.0",
            "tailwindcss": "4.1.12",
            "typescript": "5.9.2",
            "vite": "6.3.5"
          }
        }
        """;

    private static Dictionary<string, string> ParseFileMap(string snapshotJson) =>
        SiteFileMap.Parse(snapshotJson);

    // A build may emit symlinks into dist/ (e.g. pointing at /proc, secrets, or
    // sibling paths); never upload a symlink's target as an artifact.
    private static bool IsSymlink(string path)
    {
        try { return File.ResolveLinkTarget(path, returnFinalTarget: false) is not null; }
        catch { return false; }
    }

    private enum BuildPhase { Install, Build }

    /// <summary>
    /// Resolves which package manager to drive from the committed lockfile and
    /// whether the project defines its own build script. Install always runs with
    /// lifecycle scripts disabled so no package can execute code during install.
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
                    "install --frozen-lockfile --ignore-scripts", "install --no-frozen-lockfile --ignore-scripts",
                    _ => hasBuildScript ? "run build" : "exec vite build");
            }
            if (Has("yarn.lock"))
            {
                return new PackageManager("yarn", "yarn",
                    "install --frozen-lockfile --ignore-scripts", "install --ignore-scripts",
                    _ => hasBuildScript ? "run build" : "exec vite build");
            }
            // Default to npm. `npm ci` requires a lockfile in sync; fall back to install.
            // --legacy-peer-deps: npm's strict peer resolution rejects trees that pnpm
            // (which the palette was designed for) installs fine — e.g. react-day-picker
            // declaring react ^16/17/18 against the palette's react 19. Tolerate it so
            // legacy no-lockfile sites on the synthesized palette still build.
            var hasLock = Has("package-lock.json");
            return new PackageManager("npm", "npm",
                hasLock ? "ci --no-audit --no-fund --ignore-scripts --legacy-peer-deps" : "install --no-audit --no-fund --ignore-scripts --legacy-peer-deps",
                hasLock ? "install --no-audit --no-fund --ignore-scripts --legacy-peer-deps" : null,
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

    /// <summary>
    /// Runs one build phase either inside a sandbox container (production) or, when
    /// no sandbox image is configured, directly with a scrubbed environment (dev).
    /// </summary>
    private Task RunPhaseAsync(
        BuildPhase phase, string pmExe, string pmArgs, string workDir, string label,
        TimeSpan timeout, StringBuilder log, CancellationToken ct)
    {
        // The registry is only consulted while installing; the build runs offline.
        var containerEnv = SandboxEnv(includeRegistry: phase == BuildPhase.Install);
        var pmArgv = pmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (_sandbox.Enabled)
        {
            // Inside the sandbox the work dir is bind-mounted at /work.
            containerEnv["HOME"] = "/work";
            var argv = _sandbox.BuildDockerArgv(phase == BuildPhase.Install, pmExe, pmArgv, workDir, containerEnv);
            // The `docker` client itself gets only PATH + HOME (+ DOCKER_HOST, added
            // below) — never the build env, never any secret.
            var cliEnv = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin",
                ["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? workDir,
            };
            return RunAsync("docker", argv, workDir, label, timeout, log, cliEnv, ct);
        }
        // Dev direct path: the package manager runs here, so HOME is the (discarded)
        // work dir — package managers write cache/state under it.
        containerEnv["HOME"] = workDir;
        return RunAsync(pmExe, pmArgv, workDir, label, timeout, log, containerEnv, ct);
    }

    /// <summary>
    /// The <b>only</b> environment variables an untrusted build is allowed to see.
    /// Everything else (Postgres/MinIO/Vault credentials inherited from the service
    /// process) is deliberately excluded. PATH is preserved so the package manager
    /// resolves; HOME is redirected into the (discarded) work dir.
    /// </summary>
    private Dictionary<string, string> SandboxEnv(bool includeRegistry)
    {
        // HOME is set by the caller per execution path (/work in the sandbox, the
        // work dir for the dev direct path).
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin",
            ["CI"] = "1",
            ["npm_config_audit"] = "false",
            ["npm_config_fund"] = "false",
            ["npm_config_update_notifier"] = "false",
            ["ADBLOCK"] = "1",
            ["DISABLE_OPENCOLLECTIVE"] = "1",
        };
        if (includeRegistry && _sandbox.Registry is { Length: > 0 } registry)
        {
            env["npm_config_registry"] = registry;
        }
        return env;
    }

    /// <summary>
    /// Spawns a process with a <b>fully replaced</b> environment (never the parent's,
    /// so secrets can't leak in), passing arguments via <see cref="ProcessStartInfo.ArgumentList"/>
    /// (no shell, no interpolation), draining both pipes, and enforcing a hard timeout
    /// with a process-tree kill. Used both for the direct dev path and to launch `docker`.
    /// </summary>
    private async Task RunAsync(
        string fileName, IReadOnlyList<string> argv, string workDir, string label,
        TimeSpan timeout, StringBuilder log, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        log.AppendLine($"$ {fileName} {string.Join(' ', argv)}");
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workDir,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in argv)
        {
            startInfo.ArgumentList.Add(a);
        }
        // ProcessStartInfo.Environment is pre-seeded with the parent process's full
        // environment. Clear it so an untrusted build (and the `docker` client we use
        // to launch it) inherits nothing — then add only the allow-listed values.
        startInfo.Environment.Clear();
        foreach (var (k, v) in env)
        {
            startInfo.Environment[k] = v;
        }
        // The `docker` CLI needs to know where the daemon is; carry it (and only it)
        // through from the service env so the client can reach the socket proxy.
        if (_sandbox.Enabled && _sandbox.DockerHost is { Length: > 0 } host)
        {
            startInfo.Environment["DOCKER_HOST"] = host;
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
            logger.LogWarning("Build step `{File} {Args}` failed: {Output}", fileName, string.Join(' ', argv), output);
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
