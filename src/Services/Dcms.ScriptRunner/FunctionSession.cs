using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Dcms.ScriptRunner;

/// <summary>
/// Runs one function for one caller over one WebSocket (ADR 0029). The caller sends
/// <c>{"type":"start","mode":"run"|"describe","bundle",…,"input","context","limits"}</c>, answers each
/// <c>{"type":"call"}</c> it is sent with <c>{"type":"reply"}</c>, receives the function's
/// <c>{"type":"log"}</c> lines as they happen, and finally one <c>{"type":"done","status":"ok"|"error"|"limit"}</c>.
///
/// <para>The function runs in a fresh Deno process with no permissions (<see cref="Confined"/>):
/// this host only relays. It holds no credentials and reaches
/// nothing, so what a call may do is decided by the caller's broker, never here. What is decided
/// here is how much a run may take of this shared box: wall time, heap, host calls, bytes moved
/// through them, log and result size. Past any of them the process is killed and the run ends as
/// <c>limit</c>.</para>
/// </summary>
public sealed partial class FunctionSession : IDisposable
{
    [GeneratedRegex(@"\b(?:[A-Z][A-Za-z]*Error|Aborted|OutOfMemoryException)\b")]
    private static partial Regex ErrorName();

    private readonly ScriptRunnerOptions _o;
    private readonly ILogger<FunctionSession> logger;
    private readonly SemaphoreSlim _gate;
    private readonly Timer _sweeper;

    public FunctionSession(IOptions<ScriptRunnerOptions> options, ILogger<FunctionSession> logger)
    {
        _o = options.Value;
        this.logger = logger;
        _gate = new SemaphoreSlim(_o.MaxSessions);
        // Often: a kept process found busy while idle is retired by this (#607).
        _sweeper = new Timer(_ => RetireIdle(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>Kept processes go with the service.</summary>
    public void Dispose()
    {
        _sweeper.Dispose();
        List<Worker> all;
        lock (_idle)
        {
            all = [.. _idle.Values.SelectMany(s => s)];
            _idle.Clear();
        }
        foreach (var worker in all) Retire(worker);
    }

    public async Task RunAsync(WebSocket socket, CancellationToken ct)
    {
        var start = await ReceiveAsync(socket, ct, _o.MaxStartBytes);
        var relay = new Relay();
        var done = start.Message is { } message
            ? await RunAsync(socket, message, relay, ct)
            : Done("error", error: start.Oversized ? "The start message is too large." : "The session must begin with a start message.");
        if (socket.State == WebSocketState.Open)
        {
            await SendAsync(socket, done, ct);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
        }
        // The reply relay is never cancelled — a cancelled receive aborts the socket, and the done
        // above could not be sent — so it ends when the caller closes its side, or soon after.
        try
        {
            await relay.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            // The request ends; Kestrel aborts the socket and the receive with it.
        }
    }

    private sealed class Relay
    {
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private async Task<JsonObject> RunAsync(WebSocket socket, JsonObject start, Relay relay, CancellationToken ct)
    {
        // deno-ts: an esbuild bundle (ADR 0029). dotnet: a compiled assembly and the class to run,
        // on .NET's WebAssembly runtime inside the same permission-less Deno (ADR 0030).
        var dotnet = Text(start["runtime"]) == "dotnet";
        var code = dotnet ? Text(start["assembly"]) : Text(start["bundle"]);
        if (Text(start["type"]) != "start" || code is null || (dotnet && Text(start["class"]) is null))
        {
            return Done("error", error: "The session must begin with a start message.");
        }
        if (code.Length + (Text(start["pdb"])?.Length ?? 0) > _o.MaxBundleChars)
        {
            return Done("limit", error: $"A function's code is at most {_o.MaxBundleChars:N0} characters.");
        }
        var limits = start["limits"] as JsonObject;
        var timeout = TimeSpan.FromMilliseconds(Clamp(limits?["timeoutMs"], 100, _o.MaxSessionTimeoutMs, 30_000));
        var heap = Clamp(limits?["heapMb"], 32, _o.MaxSessionHeapMegabytes, _o.DefaultSessionHeapMegabytes);

        // A deployed C# function's process is kept for its next run (#607): booting .NET is most of
        // a run's cost. The caller names the deployment; the key also holds the code itself, so a
        // process only ever runs the one assembly it loaded.
        var warmKey = dotnet && _o.MaxWarmRuns > 0 && Text(start["warm"]) is { Length: > 0 and <= 200 } owner
            ? $"{owner}:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(code)))[..32]}:{heap}"
            : null;
        var worker = warmKey is null ? null : TakeIdle(warmKey);
        var reused = worker is not null;
        if (worker is null)
        {
            await AcquireSlotAsync(ct);
            worker = Spawn(dotnet, heap);
        }
        var process = worker.Process;
        var keep = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            var run = new Budget(process);
            worker.Runs++;

            try
            {
                await WriteLineAsync(process, dotnet
                    ? new JsonObject
                    {
                        ["type"] = "start",
                        ["mode"] = "run",
                        // A process kept for this deployment has its assembly loaded already.
                        ["assembly"] = reused ? null : code,
                        ["pdb"] = reused ? null : Text(start["pdb"]),
                        // Its NuGet packages' assemblies (ADR 0031), loaded before it.
                        ["packages"] = reused ? null : start["packages"]?.DeepClone(),
                        ["class"] = Text(start["class"]),
                        ["input"] = start["input"]?.DeepClone(),
                        ["context"] = start["context"]?.DeepClone(),
                        ["serve"] = warmKey is not null,
                    }
                    : new JsonObject
                    {
                        ["type"] = "start",
                        ["mode"] = Text(start["mode"]) == "describe" ? "describe" : "run",
                        ["bundle"] = code,
                        ["input"] = start["input"]?.DeepClone(),
                        ["context"] = start["context"]?.DeepClone(),
                    }, deadline.Token);
            }
            catch (IOException)
            {
                // The process ended before taking its start message: its exit says why, below.
            }

            relay.Task = RelayRepliesAsync(socket, process, run, ct);
            JsonObject? done;
            try
            {
                done = await RelayCallsAsync(socket, worker.Output, run, deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Confined.Kill(process);
                return run.Failure ?? Done("limit", error: $"The function ran longer than {timeout.TotalSeconds:0.#} s.");
            }

            if (run.Failure is { } failure)
            {
                return failure;
            }
            if (done is not null)
            {
                run.Finished = true;
                // Kept only while healthy: a limit hit, a run budget spent or a crash retires it.
                keep = warmKey is not null && Text(done["status"]) != "limit" && !process.HasExited && worker.Runs < _o.MaxWarmRuns;
                return done;
            }
            try
            {
                // Its output can end long before the process does (it may close stdout and spin): the deadline still holds.
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Confined.Kill(process);
                return Done("limit", error: $"The function ran longer than {timeout.TotalSeconds:0.#} s.");
            }
            var errors = await worker.Stderr;
            // 133: V8 aborted (SIGTRAP) at its heap limit; 134: V8's fatal out-of-memory abort (SIGABRT), seen
            // when the box itself is short of memory; 137: the kernel's OOM killer.
            if (process.ExitCode is 133 or 134 or 137 || errors.Contains("heap limit", StringComparison.OrdinalIgnoreCase)
                || errors.Contains("out of memory", StringComparison.OrdinalIgnoreCase))
            {
                return Done("limit", error: $"The function used more than {heap} MB of memory.");
            }
            // The exit code only: stderr can quote the function and its data, and logs leave this box.
            logger.LogWarning("deno exited with {ExitCode} ({StderrChars} characters on stderr)", process.ExitCode, errors.Length);
            // Which JS errors it died of, by class name only (never their messages): what tells a
            // runtime failure from one of the function's own when the exit code alone cannot.
            var died = string.Join(", ", ErrorName().Matches(errors).Select(m => m.Value).Distinct().Take(3));
            return Done("error", error: $"The function could not run (exit {process.ExitCode}{(died.Length > 0 ? $": {died}" : "")}).");
        }
        finally
        {
            if (keep)
            {
                ReturnIdle(warmKey!, worker);
            }
            else
            {
                Retire(worker);
            }
        }
    }

    /// <summary>One Deno process and what reads it: for one run, or kept for a deployment's runs (#607).</summary>
    private sealed class Worker(Process process, DirectoryInfo cache, LineReader output, Task<string> stderr)
    {
        public Process Process { get; } = process;
        public DirectoryInfo Cache { get; } = cache;
        public LineReader Output { get; } = output;
        public Task<string> Stderr { get; } = stderr;
        public int Runs { get; set; }
        public long IdleSince { get; set; }
        public TimeSpan IdleCpu { get; set; }

        /// <summary>
        /// A kept process must be quiet between runs: its done line is the end of what it may do.
        /// Work it left running (a timer, a background task) would escape the run's wall-time
        /// limit, so CPU spent while idle retires it.
        /// </summary>
        public bool BusyWhileIdle(TimeSpan allowance)
        {
            try
            {
                Process.Refresh();
                return Process.HasExited || Process.TotalProcessorTime - IdleCpu > allowance;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>What a kept process may spend while idle: V8's own housekeeping, nothing more.</summary>
    private static readonly TimeSpan IdleAllowance = TimeSpan.FromMilliseconds(200);

    private readonly Dictionary<string, Stack<Worker>> _idle = new(StringComparer.Ordinal);

    private Worker Spawn(bool dotnet, int heap)
    {
        var cache = Directory.CreateTempSubdirectory("fn-");
        try
        {
            // Deno's own start-up reserves most of the data limit (see DataLimitMegabytes); the
            // function's heap, and as much again off it for buffers, come on top.
            // .NET's runtime is one large Wasm module: V8's optimizing tier needs ~900 MB of data
            // limit to compile it, its baseline tier fits a TypeScript run's (ADR 0030). The bytes
            // imports are how its files load without a read permission (dotnet-harness.js).
            var process = Confined.Start(_o.DenoPath,
                dotnet
                    // --location: .NET's HTTP handler probes for streaming with new Request(""),
                    // which a page resolves against its location and Deno refuses without one.
                    ? ["run", "--no-prompt", "--no-remote", "--no-npm", "--no-config", "--no-lock", "--unstable-raw-imports",
                       "--location=https://function.invalid/",
                       $"--v8-flags=--max-old-space-size={heap},--liftoff-only", _o.DotnetHarnessPath]
                    : ["run", "--no-prompt", "--no-remote", "--no-npm", "--no-config", "--no-lock",
                       $"--v8-flags=--max-old-space-size={heap}", _o.SessionHarnessPath],
                (_o.DataLimitMegabytes + 2L * heap) * 1024 * 1024,
                new Dictionary<string, string> { ["DENO_DIR"] = cache.FullName });
            return new Worker(process, cache, new LineReader(process.StandardOutput, _o.MaxMessageBytes),
                Confined.ReadBoundedAsync(process.StandardError, 4_000, onOverflow: null));
        }
        catch
        {
            cache.Delete(recursive: true);
            _gate.Release();
            throw;
        }
    }

    /// <summary>
    /// A slot for a new process. Kept processes hold theirs, so the box never runs more than
    /// MaxSessions Denos: when every slot is taken and some are only idle, the longest idle goes.
    /// </summary>
    private async Task AcquireSlotAsync(CancellationToken ct)
    {
        while (!await _gate.WaitAsync(0, ct))
        {
            if (!RetireOldestIdle())
            {
                await _gate.WaitAsync(ct);
                return;
            }
        }
    }

    private Worker? TakeIdle(string key)
    {
        lock (_idle)
        {
            while (_idle.TryGetValue(key, out var stack) && stack.TryPop(out var worker))
            {
                if (stack.Count == 0) _idle.Remove(key);
                if (!worker.BusyWhileIdle(IdleAllowance)) return worker;
                logger.LogWarning("A kept function process was busy while idle; retired.");
                Retire(worker);
            }
            return null;
        }
    }

    private void ReturnIdle(string key, Worker worker)
    {
        worker.IdleSince = Environment.TickCount64;
        try
        {
            worker.Process.Refresh();
            worker.IdleCpu = worker.Process.TotalProcessorTime;
        }
        catch (InvalidOperationException)
        {
            Retire(worker);
            return;
        }
        lock (_idle)
        {
            if (!_idle.TryGetValue(key, out var stack)) _idle[key] = stack = new Stack<Worker>();
            stack.Push(worker);
        }
    }

    private bool RetireOldestIdle()
    {
        Worker? oldest = null;
        lock (_idle)
        {
            foreach (var (key, stack) in _idle)
            {
                foreach (var worker in stack)
                {
                    if (oldest is null || worker.IdleSince < oldest.IdleSince) oldest = worker;
                }
            }
            if (oldest is null) return false;
            foreach (var (key, stack) in _idle.ToList())
            {
                var kept = new Stack<Worker>(stack.Where(w => w != oldest).Reverse());
                if (kept.Count == 0) _idle.Remove(key);
                else _idle[key] = kept;
            }
        }
        Retire(oldest);
        return true;
    }

    /// <summary>Kept processes idle past <see cref="ScriptRunnerOptions.WarmIdleSeconds"/>, or busy while idle, are retired; called on a timer.</summary>
    public void RetireIdle()
    {
        var limit = Environment.TickCount64 - _o.WarmIdleSeconds * 1000L;
        List<Worker> stale;
        lock (_idle)
        {
            stale = [.. _idle.Values.SelectMany(s => s).Where(w => w.IdleSince < limit || w.BusyWhileIdle(IdleAllowance))];
            foreach (var (key, stack) in _idle.ToList())
            {
                var kept = new Stack<Worker>(stack.Where(w => !stale.Contains(w)).Reverse());
                if (kept.Count == 0) _idle.Remove(key);
                else _idle[key] = kept;
            }
        }
        foreach (var worker in stale) Retire(worker);
    }

    private void Retire(Worker worker)
    {
        if (!worker.Process.HasExited)
        {
            Confined.Kill(worker.Process);
        }
        worker.Process.Dispose();
        try
        {
            worker.Cache.Delete(recursive: true);
        }
        catch (IOException e)
        {
            logger.LogWarning(e, "Could not delete {Cache}", worker.Cache.FullName);
        }
        _gate.Release();
    }

    /// <summary>Deno to the caller: host calls and log lines, until the function's done line or the end of its output.</summary>
    private async Task<JsonObject?> RelayCallsAsync(WebSocket socket, LineReader lines, Budget run, CancellationToken ct)
    {
        while (await lines.ReadAsync(ct) is { } line)
        {
            if (line.Length > _o.MaxMessageBytes)
            {
                return run.Fail($"A host call is at most {_o.MaxMessageBytes:N0} bytes.");
            }
            if (Parse(line) is not { } message)
            {
                // Whatever the function wrote to stdout itself; the protocol's lines are JSON.
                continue;
            }
            switch (Text(message["type"]))
            {
                case "call":
                    if (++run.Calls > _o.MaxCalls)
                    {
                        return run.Fail($"A function makes at most {_o.MaxCalls:N0} host calls.");
                    }
                    if ((run.IoBytes += line.Length) > _o.MaxIoBytes)
                    {
                        return run.Fail($"A function moves at most {_o.MaxIoBytes / (1024 * 1024)} MB through host calls.");
                    }
                    await SendAsync(socket, new JsonObject
                    {
                        ["type"] = "call",
                        ["id"] = message["id"]?.DeepClone(),
                        ["op"] = Text(message["op"]),
                        ["args"] = message["args"]?.DeepClone(),
                    }, ct);
                    break;

                case "log" when !run.LogCut:
                    var text = Text(message["message"]) ?? "";
                    var room = _o.MaxLogChars - run.LogChars;
                    // The first line that does not fit says the log was cut, then nothing more is sent.
                    var truncated = text.Length > room;
                    run.LogCut = truncated;
                    run.LogChars += Math.Min(text.Length, room);
                    await SendAsync(socket, new JsonObject
                    {
                        ["type"] = "log",
                        ["level"] = Text(message["level"]) is "info" or "debug" or "warn" or "error" ? Text(message["level"]) : "log",
                        ["message"] = truncated ? text[..room] + $" … (the log is cut at {_o.MaxLogChars:N0} characters)" : text,
                    }, ct);
                    break;

                case "done":
                    if (message["ok"]?.GetValueKind() != JsonValueKind.True)
                    {
                        // .NET reports running out of its memory itself, as an exception it survives.
                        return Done(message["limit"]?.GetValueKind() == JsonValueKind.True ? "limit" : "error",
                            error: Text(message["error"]) ?? "The function failed.");
                    }
                    var result = message["result"]?.DeepClone();
                    if ((result?.ToJsonString().Length ?? 0) > _o.MaxResultChars)
                    {
                        return run.Fail($"A function's result is at most {_o.MaxResultChars:N0} characters.");
                    }
                    return Done("ok", result);
            }
        }
        return null;
    }

    /// <summary>
    /// The caller to Deno: replies to host calls, until the caller closes. A caller that goes away
    /// mid-run ends the run. <paramref name="ct"/> is the request's: see <see cref="Relay"/>.
    /// </summary>
    private async Task RelayRepliesAsync(WebSocket socket, Process process, Budget run, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var received = await ReceiveAsync(socket, ct);
                if (received.Oversized)
                {
                    run.Fail($"A host call's reply is at most {_o.MaxMessageBytes:N0} bytes.");
                    return;
                }
                if (received.Message is not { } reply)
                {
                    // A caller gone mid-run ends the run; one that closes after the answer leaves a
                    // kept process to its next run.
                    if (!run.Finished) Confined.Kill(process);
                    return;
                }
                if (Text(reply["type"]) != "reply")
                {
                    continue;
                }
                var line = new JsonObject
                {
                    ["type"] = "reply",
                    ["id"] = reply["id"]?.DeepClone(),
                    ["ok"] = reply["ok"]?.GetValueKind() == JsonValueKind.True,
                    ["result"] = reply["result"]?.DeepClone(),
                    ["error"] = Text(reply["error"]),
                };
                var size = line.ToJsonString().Length;
                if ((run.IoBytes += size) > _o.MaxIoBytes)
                {
                    run.Fail($"A function moves at most {_o.MaxIoBytes / (1024 * 1024)} MB through host calls.");
                    return;
                }
                await WriteLineAsync(process, line, ct);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or WebSocketException
                                       or ObjectDisposedException or InvalidOperationException)
        {
            // The run is over, the process gone, or the caller: each ends the relay, nothing to report from here.
        }
    }

    private static async Task WriteLineAsync(Process process, JsonObject message, CancellationToken ct)
    {
        await process.StandardInput.WriteAsync((message.ToJsonString() + "\n").AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
    }

    private static Task SendAsync(WebSocket socket, JsonObject message, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, endOfMessage: true, ct);

    private readonly record struct Received(JsonObject? Message, bool Oversized = false);

    private async Task<Received> ReceiveAsync(WebSocket socket, CancellationToken ct, long? max = null)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await socket.ReceiveAsync(chunk, ct);
            if (read.MessageType == WebSocketMessageType.Close)
            {
                return new Received(null);
            }
            if (buffer.Length + read.Count > (max ?? _o.MaxMessageBytes))
            {
                return new Received(null, Oversized: true);
            }
            buffer.Write(chunk, 0, read.Count);
            if (read.EndOfMessage)
            {
                return new Received(Parse(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length)));
            }
        }
    }

    private static JsonObject? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int Clamp(JsonNode? node, int min, int max, int fallback) =>
        Math.Clamp(node is JsonValue value && value.TryGetValue<int>(out var n) ? n : fallback, min, max);

    private static JsonObject Done(string status, JsonNode? result = null, string? error = null) =>
        new() { ["type"] = "done", ["status"] = status, ["result"] = result, ["error"] = error };

    /// <summary>What one run has used so far, and the first limit it broke.</summary>
    private sealed class Budget(Process process)
    {
        public int Calls;
        public long IoBytes;
        public volatile bool Finished;
        public int LogChars;
        public bool LogCut;
        public JsonObject? Failure { get; private set; }

        public JsonObject Fail(string message)
        {
            Failure ??= Done("limit", error: message);
            Confined.Kill(process);
            return Failure;
        }
    }

    /// <summary>Lines from a stream, none kept past <c>max</c> characters: a longer one comes back cut, one past the cap.</summary>
    private sealed class LineReader(StreamReader reader, int max)
    {
        private readonly char[] _buffer = new char[16 * 1024];
        private int _start;
        private int _end;

        public async Task<string?> ReadAsync(CancellationToken ct)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = await reader.ReadAsync(_buffer.AsMemory(), ct);
                    if (_end == 0)
                    {
                        return line.Length > 0 ? line.ToString() : null;
                    }
                }
                var newline = Array.IndexOf(_buffer, '\n', _start, _end - _start);
                var stop = newline < 0 ? _end : newline;
                if (line.Length <= max)
                {
                    line.Append(_buffer, _start, Math.Min(stop - _start, max + 1 - line.Length));
                }
                _start = newline < 0 ? _end : newline + 1;
                if (newline >= 0)
                {
                    return line.ToString();
                }
            }
        }
    }
}
