using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Dcms.ScriptRunner;

public sealed class ScriptRunnerOptions
{
    public string DenoPath { get; set; } = "deno";
    public string HarnessPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "harness.js");

    /// <summary>Scripts running at once; the rest wait their turn (each is a process with its own heap).</summary>
    public int MaxConcurrency { get; set; } = 4;

    public int HeapMegabytes { get; set; } = 64;
    public int MaxTimeoutMs { get; set; } = 5_000;
    public int MaxCodeChars { get; set; } = 64_000;
    public int MaxOutputChars { get; set; } = 64_000;
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
/// A deadline, a heap cap and an output cap each end it; the host kills the whole process tree.
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
            var start = new ProcessStartInfo(_o.DenoPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in (string[])["run", "--no-prompt", "--no-remote", "--no-npm", "--no-config", "--no-lock",
                         $"--v8-flags=--max-old-space-size={_o.HeapMegabytes}", _o.HarnessPath])
            {
                start.ArgumentList.Add(arg);
            }
            // Nothing of this process's environment reaches the script's.
            start.Environment.Clear();
            start.Environment["DENO_DIR"] = cache.FullName;
            start.Environment["NO_COLOR"] = "1";

            process = Process.Start(start) ?? throw new InvalidOperationException("deno did not start.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);

            var running = process;
            var overflowed = false;
            var stdout = ReadBoundedAsync(process.StandardOutput, _o.MaxOutputChars + 256, () =>
            {
                overflowed = true;
                Kill(running);
            });
            var stderr = ReadBoundedAsync(process.StandardError, 4_000, onOverflow: null);

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
                Kill(process);
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
                if (process.ExitCode == 133 || errors.Contains("heap limit", StringComparison.OrdinalIgnoreCase)
                    || errors.Contains("out of memory", StringComparison.OrdinalIgnoreCase))
                {
                    return RunResult.Limit($"The script used more than {_o.HeapMegabytes} MB of memory.");
                }
                logger.LogWarning("deno exited with {ExitCode}: {Stderr}", process.ExitCode, errors);
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
                Kill(process);
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

    /// <summary>Reads a stream to its end, keeping at most <paramref name="max"/> characters; past that, <paramref name="onOverflow"/> once.</summary>
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int max, Action? onOverflow)
    {
        var text = new StringBuilder();
        var buffer = new char[8_192];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (text.Length + read > max)
            {
                if (onOverflow is not null)
                {
                    onOverflow();
                    return text.ToString();
                }
                continue;
            }
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}
