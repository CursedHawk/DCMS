using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Dcms.ScriptRunner;

public sealed class ScriptRunnerOptions
{
    public string DenoPath { get; set; } = "deno";
    public string HarnessPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "harness.js");

    /// <summary>Scripts running at once; the rest wait their turn. Two fit the container's 640 MB with room to spare.</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>V8's own heap. It does not cover ArrayBuffers or Wasm memory — <see cref="DataLimitMegabytes"/> does.</summary>
    public int HeapMegabytes { get; set; } = 64;

    /// <summary>
    /// RLIMIT_DATA for each Deno process: every private writable mapping, ArrayBuffers included,
    /// enforced by the kernel. Deno 2.5's own startup reserves ~600 MB of it, so this leaves a
    /// script roughly 40–80 MB beyond the heap; below ~600 Deno does not start at all.
    /// </summary>
    public int DataLimitMegabytes { get; set; } = 640;
    public int MaxTimeoutMs { get; set; } = 5_000;
    public int MaxCodeChars { get; set; } = 64_000;
    public int MaxOutputChars { get; set; } = 64_000;

    // Functions (ADR 0029): sessions and builds.

    public string SessionHarnessPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "session-harness.js");

    /// <summary>The static <c>@dcms/functions</c> runtime every bundle imports in place of the SDK.</summary>
    public string RuntimePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "sdk-runtime.js");
    public string EsbuildPath { get; set; } = "esbuild";

    /// <summary>Functions running at once, apart from flow scripts. Callers cap each tenant below this.</summary>
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

/// <param name="Code">The module source; TypeScript or JavaScript, its default export is <c>main(ctx)</c>.</param>
/// <param name="Language"><c>ts</c> or <c>js</c>.</param>
/// <param name="Ctx">What <c>main</c> receives.</param>
public sealed record RunRequest(string Code, JsonNode? Ctx, string Language = "ts", int TimeoutMs = 2_000);

/// <param name="Status"><c>ok</c>; <c>error</c> (the script threw, or does not compile); <c>limit</c> (time, memory or output).</param>
public sealed record RunResult(string Status, JsonNode? Result = null, string? Error = null)
{
    public static RunResult Limit(string message) => new("limit", Error: message);
    public static RunResult Failed(string message) => new("error", Error: message);
}

/// <summary>
/// Runs one flow script in a fresh Deno process with no permissions at all (ADR 0025): no network,
/// files, environment, subprocesses or FFI. The process gets a cleared environment and its own
/// empty DENO_DIR, which is deleted afterwards, so no two runs share anything — not even a cache.
/// A deadline, a heap cap, a kernel limit on all writable memory and an output cap each end it;
/// the host kills the whole process tree. It also never dumps core (a crash would otherwise fill
/// the tmpfs every run shares) and is the first thing the kernel kills if memory runs out, ahead
/// of this host.
/// </summary>
public sealed class DenoSandbox(IOptions<ScriptRunnerOptions> options, ILogger<DenoSandbox> logger)
{
    private readonly ScriptRunnerOptions _o = options.Value;
    private readonly SemaphoreSlim _gate = new(options.Value.MaxConcurrency);

    public async Task<RunResult> RunAsync(RunRequest request, CancellationToken ct)
    {
        if (request.Code.Length > _o.MaxCodeChars)
        {
            return RunResult.Limit($"A script is at most {_o.MaxCodeChars:N0} characters.");
        }
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMs, 100, _o.MaxTimeoutMs));

        await _gate.WaitAsync(ct);
        var cache = Directory.CreateTempSubdirectory("deno-");
        Process? process = null;
        try
        {
            process = Confined.Start(_o.DenoPath,
                ["run", "--no-prompt", "--no-remote", "--no-npm", "--no-config", "--no-lock",
                 $"--v8-flags=--max-old-space-size={_o.HeapMegabytes}", _o.HarnessPath],
                _o.DataLimitMegabytes * 1024L * 1024,
                new Dictionary<string, string> { ["DENO_DIR"] = cache.FullName });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);

            var running = process;
            var overflowed = false;
            var stdout = Confined.ReadBoundedAsync(process.StandardOutput, _o.MaxOutputChars + 256, () =>
            {
                overflowed = true;
                Confined.Kill(running);
            });
            var stderr = Confined.ReadBoundedAsync(process.StandardError, 4_000, onOverflow: null);

            try
            {
                try
                {
                    await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { code = request.Code, ctx = request.Ctx, language = request.Language == "js" ? "js" : "ts" }).AsMemory(), deadline.Token);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The process ended before taking all of its input: its exit says why, below.
                }
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Confined.Kill(process);
                return RunResult.Limit($"The script ran longer than {timeout.TotalSeconds:0.#} s.");
            }

            var output = await stdout;
            var errors = await stderr;
            if (overflowed)
            {
                return RunResult.Limit($"The script's result is larger than {_o.MaxOutputChars:N0} characters.");
            }
            if (process.ExitCode != 0)
            {
                // 133: V8 aborted (SIGTRAP) at its heap limit; 134: V8's fatal out-of-memory abort (SIGABRT), seen
                // when the box itself is short of memory; 137: the kernel's OOM killer.
                if (process.ExitCode is 133 or 134 or 137 || errors.Contains("heap limit", StringComparison.OrdinalIgnoreCase)
                    || errors.Contains("out of memory", StringComparison.OrdinalIgnoreCase))
                {
                    return RunResult.Limit($"The script used more than {_o.HeapMegabytes} MB of memory.");
                }
                // The exit code only: stderr can quote the script and the run's data, and logs leave this box.
                logger.LogWarning("deno exited with {ExitCode} ({StderrChars} characters on stderr)", process.ExitCode, errors.Length);
                return RunResult.Failed($"The script could not run (exit {process.ExitCode}).");
            }

            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (line is null || JsonNode.Parse(line) is not JsonObject reply)
            {
                return RunResult.Failed("The script produced no result.");
            }
            return reply["ok"]?.GetValue<bool>() == true
                ? new RunResult("ok", reply["result"]?.DeepClone())
                : RunResult.Failed(reply["error"]?.GetValue<string>() ?? "The script failed.");
        }
        finally
        {
            if (process is { HasExited: false })
            {
                Confined.Kill(process);
            }
            process?.Dispose();
            try
            {
                cache.Delete(recursive: true);
            }
            catch (IOException e)
            {
                logger.LogWarning(e, "Could not delete {Cache}", cache.FullName);
            }
            _gate.Release();
        }
    }
}
