namespace Dcms.SiteBuilder;

/// <summary>
/// Configuration for the per-build sandbox, read once from the process environment.
/// A build is untrusted code, so install + build run in an ephemeral container the
/// builder launches over the Docker API (through a docker-socket-proxy that exposes
/// only container operations) rather than in the builder process itself.
///
/// Environment variables:
/// <list type="bullet">
///   <item><c>DCMS_BUILD_SANDBOX_IMAGE</c> — image that runs the build (node + pnpm/yarn).
///     When empty the sandbox is disabled (dev fallback).</item>
///   <item><c>DCMS_BUILD_WORK_HOST_ROOT</c> — the <b>host</b> path that the builder's
///     work root (<c>DCMS_BUILD_WORK_DIR</c>) is bind-mounted from. Required to bind the
///     per-build dir into the sandbox, because the shared Docker daemon resolves bind
///     sources against the host, not against the builder container.</item>
///   <item><c>DCMS_BUILD_REQUIRE_SANDBOX=true</c> — fail a build rather than run it
///     unsandboxed (set in production).</item>
///   <item><c>DCMS_BUILD_RUNTIME</c> — optional container runtime, e.g. <c>runsc</c> (gVisor).</item>
///   <item><c>DCMS_BUILD_NETWORK_INSTALL</c> — docker network for the install phase
///     (default: the daemon default bridge). Install runs <c>--ignore-scripts</c> so no
///     untrusted code executes even with network.</item>
///   <item><c>DCMS_BUILD_NETWORK_BUILD</c> — docker network for the build phase
///     (default <c>none</c>): the build executes site code, so it gets no network.</item>
///   <item><c>DCMS_BUILD_MEM</c> / <c>DCMS_BUILD_CPUS</c> / <c>DCMS_BUILD_PIDS</c> —
///     per-build limits (defaults 2g / 2 / 512).</item>
///   <item><c>DCMS_BUILD_USER</c> — optional uid:gid the sandbox runs as.</item>
///   <item><c>DCMS_BUILD_REGISTRY</c> — optional npm registry URL for installs.</item>
/// </list>
/// </summary>
public sealed class SandboxOptions
{
    public string? Image { get; private init; }
    public string? WorkHostRoot { get; private init; }
    public bool Required { get; private init; }
    public string? Runtime { get; private init; }
    public string? DockerHost { get; private init; }
    public string? Registry { get; private init; }
    private string? InstallNetwork { get; init; }
    private string BuildNetwork { get; init; } = "none";
    private string Memory { get; init; } = "2g";
    private string Cpus { get; init; } = "2";
    private string Pids { get; init; } = "512";
    private string? User { get; init; }

    /// <summary>Sandboxing is active when both an image and a host work root are known.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Image) && !string.IsNullOrWhiteSpace(WorkHostRoot);

    public static SandboxOptions FromEnvironment()
    {
        static string? Env(string k)
        {
            var v = Environment.GetEnvironmentVariable(k);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        return new SandboxOptions
        {
            Image = Env("DCMS_BUILD_SANDBOX_IMAGE"),
            WorkHostRoot = Env("DCMS_BUILD_WORK_HOST_ROOT"),
            Required = string.Equals(Env("DCMS_BUILD_REQUIRE_SANDBOX"), "true", StringComparison.OrdinalIgnoreCase),
            Runtime = Env("DCMS_BUILD_RUNTIME"),
            DockerHost = Env("DOCKER_HOST"),
            Registry = Env("DCMS_BUILD_REGISTRY"),
            InstallNetwork = Env("DCMS_BUILD_NETWORK_INSTALL"),
            BuildNetwork = Env("DCMS_BUILD_NETWORK_BUILD") ?? "none",
            Memory = Env("DCMS_BUILD_MEM") ?? "2g",
            Cpus = Env("DCMS_BUILD_CPUS") ?? "2",
            Pids = Env("DCMS_BUILD_PIDS") ?? "512",
            User = Env("DCMS_BUILD_USER"),
        };
    }

    /// <summary>
    /// Builds the <c>docker run …</c> argument vector that runs
    /// <paramref name="pmExe"/> <paramref name="pmArgv"/> inside a locked-down,
    /// ephemeral container with the per-build work dir bind-mounted at /work.
    /// </summary>
    /// <param name="install">
    /// True for the install phase (may use network to fetch dependencies; runs no
    /// untrusted code thanks to <c>--ignore-scripts</c>); false for the build phase
    /// (executes site code, so it gets no network).
    /// </param>
    public IReadOnlyList<string> BuildDockerArgv(
        bool install, string pmExe, IReadOnlyList<string> pmArgv,
        string workDir, IReadOnlyDictionary<string, string> containerEnv)
    {
        var hostDir = HostPathFor(workDir);
        var network = install ? (InstallNetwork ?? "bridge") : BuildNetwork;

        var argv = new List<string>
        {
            "run", "--rm", "--init",
            "--network", network,
            "--read-only",
            "--tmpfs", "/tmp:rw,nosuid,nodev,size=512m",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--memory", Memory,
            "--cpus", Cpus,
            "--pids-limit", Pids,
            "-v", $"{hostDir}:/work",
            "-w", "/work",
        };

        if (!string.IsNullOrWhiteSpace(Runtime))
        {
            argv.Add("--runtime");
            argv.Add(Runtime!);
        }
        if (!string.IsNullOrWhiteSpace(User))
        {
            argv.Add("--user");
            argv.Add(User!);
        }

        // Only the allow-listed build env crosses into the container. PATH is the
        // image's own, so it is never overridden from the host.
        foreach (var (k, v) in containerEnv)
        {
            if (string.Equals(k, "PATH", StringComparison.Ordinal))
            {
                continue;
            }
            argv.Add("-e");
            argv.Add($"{k}={v}");
        }

        argv.Add(Image!);
        argv.Add(pmExe);
        argv.AddRange(pmArgv);
        return argv;
    }

    /// <summary>
    /// Maps a per-build work dir (a path inside the builder container, directly under
    /// the mounted work root) to the equivalent path on the Docker host, which is what
    /// the daemon needs to satisfy the bind mount.
    /// </summary>
    private string HostPathFor(string workDir)
    {
        var leaf = Path.GetFileName(workDir.TrimEnd('/', '\\'));
        var root = WorkHostRoot!.TrimEnd('/', '\\');
        return $"{root}/{leaf}";
    }
}
