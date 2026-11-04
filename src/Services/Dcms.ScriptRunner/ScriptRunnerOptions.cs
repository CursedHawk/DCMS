namespace Dcms.ScriptRunner;

/// <summary>Function sessions and builds (ADR 0029): the sandbox's limits.</summary>
public sealed class ScriptRunnerOptions
{
    public string DenoPath { get; set; } = "deno";
    /// <summary>V8's own heap for a describe pass. It does not cover ArrayBuffers or Wasm memory — <see cref="DataLimitMegabytes"/> does.</summary>
    public int HeapMegabytes { get; set; } = 64;

    /// <summary>
    /// RLIMIT_DATA for each Deno process, before its heap: every private writable mapping,
    /// ArrayBuffers included, enforced by the kernel. Deno 2.5's own startup reserves ~600 MB of
    /// it; below that Deno does not start at all.
    /// </summary>
    public int DataLimitMegabytes { get; set; } = 640;

    public string SessionHarnessPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "session-harness.js");

    /// <summary>The static <c>@dcms/functions</c> runtime every bundle imports in place of the SDK.</summary>
    public string RuntimePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "sdk-runtime.js");
    public string EsbuildPath { get; set; } = "esbuild";

    /// <summary>Functions running at once. Callers cap each tenant below this.</summary>
    public int MaxSessions { get; set; } = 4;
    public int MaxSessionTimeoutMs { get; set; } = 300_000;
    public int DefaultSessionHeapMegabytes { get; set; } = 128;
    public int MaxSessionHeapMegabytes { get; set; } = 256;
    public int MaxBundleChars { get; set; } = 2_000_000;
    public int MaxCalls { get; set; } = 1_000;

    /// <summary>Host-call arguments and replies together, per run.</summary>
    public long MaxIoBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxLogChars { get; set; } = 64_000;
    public int MaxResultChars { get; set; } = 1_000_000;

    /// <summary>One line from Deno, or one message from the caller.</summary>
    public int MaxMessageBytes { get; set; } = 2 * 1024 * 1024;

    public int MaxBuildFiles { get; set; } = 500;
    public int MaxBuildBytes { get; set; } = 5 * 1024 * 1024;
    public int BuildTimeoutMs { get; set; } = 120_000;

    /// <summary>The type checker needs far more than a function: tsc's heap plus Deno's ~600 MB reservation.</summary>
    public int BuildDataLimitMegabytes { get; set; } = 2_048;
    public int BuildHeapMegabytes { get; set; } = 768;
}
