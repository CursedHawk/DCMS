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

    /// <summary>
    /// A C# function's harness (ADR 0030), in the published Dcms.Functions.WasmHost beside the .NET
    /// runtime it imports. The image puts it here; tests point it at their own publish.
    /// </summary>
    public string DotnetHarnessPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "dotnet", "dotnet-harness.js");

    /// <summary>The static <c>@dcms/functions</c> runtime every bundle imports in place of the SDK.</summary>
    public string RuntimePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "sdk-runtime.js");
    public string EsbuildPath { get; set; } = "esbuild";

    /// <summary>
    /// The TypeScript compiler (its <c>lib/typescript.js</c> and <c>lib/lib.*.d.ts</c>), which
    /// type-schemas.js runs to read a function's input and output types as JSON Schema.
    /// </summary>
    public string TypeScriptPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "typescript");
    public string TypeSchemasPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "type-schemas.js");

    /// <summary>The <c>dotnet</c> host that runs dcms-csc, the C# compiler of a build (ADR 0030).</summary>
    public string DotnetPath { get; set; } = "dotnet";
    public string CSharpCompilerPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "csc", "dcms-csc.dll");

    /// <summary>RLIMIT_DATA for dcms-csc; its GC is capped 256 MB below. A typical build peaks at ~230 MB.</summary>
    public int CSharpDataLimitMegabytes { get; set; } = 1_024;

    /// <summary>Functions running at once. Callers cap each tenant below this.</summary>
    public int MaxSessions { get; set; } = 4;

    /// <summary>
    /// How many runs one kept C# process serves before it is replaced (#607); 0 keeps none. A
    /// kept process holds a session slot while idle, and only ever runs one deployment's assembly.
    /// </summary>
    public int MaxWarmRuns { get; set; } = 500;

    /// <summary>How long a kept process may sit idle before it is retired.</summary>
    public int WarmIdleSeconds { get; set; } = 300;
    public int MaxSessionTimeoutMs { get; set; } = 300_000;
    public int DefaultSessionHeapMegabytes { get; set; } = 128;
    public int MaxSessionHeapMegabytes { get; set; } = 256;
    public int MaxBundleChars { get; set; } = 2_000_000;
    public int MaxCalls { get; set; } = 1_000;

    /// <summary>Host-call arguments and replies together, per run.</summary>
    public long MaxIoBytes { get; set; } = 16 * 1024 * 1024;
    public int MaxLogChars { get; set; } = 64_000;
    public int MaxResultChars { get; set; } = 1_000_000;

    /// <summary>One line from Deno, or one message from the caller.</summary>
    public int MaxMessageBytes { get; set; } = 8 * 1024 * 1024;

    public int MaxBuildFiles { get; set; } = 500;
    public int MaxBuildBytes { get; set; } = 5 * 1024 * 1024;
    public int BuildTimeoutMs { get; set; } = 120_000;

    /// <summary>The type checker needs far more than a function: tsc's heap plus Deno's ~600 MB reservation.</summary>
    public int BuildDataLimitMegabytes { get; set; } = 2_048;
    public int BuildHeapMegabytes { get; set; } = 768;
}
