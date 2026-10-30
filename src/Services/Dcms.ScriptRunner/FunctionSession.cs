using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Dcms.ScriptRunner;

/// <summary>
/// Runs one function for one caller over one WebSocket (ADR 0029). The caller sends
/// <c>{"type":"start","mode":"run"|"describe","bundle",…,"input","context","limits"}</c>, answers each
/// <c>{"type":"call"}</c> it is sent with <c>{"type":"reply"}</c>, receives the function's
/// <c>{"type":"log"}</c> lines as they happen, and finally one <c>{"type":"done","status":"ok"|"error"|"limit"}</c>.
///
/// <para>The function runs in a fresh Deno process with no permissions, exactly as a flow script
/// does (<see cref="DenoSandbox"/>): this host only relays. It holds no credentials and reaches
/// nothing, so what a call may do is decided by the caller's broker, never here. What is decided
/// here is how much a run may take of this shared box: wall time, heap, host calls, bytes moved
/// through them, log and result size. Past any of them the process is killed and the run ends as
/// <c>limit</c>.</para>
/// </summary>
public sealed class FunctionSession(IOptions<ScriptRunnerOptions> options, ILogger<FunctionSession> logger)
{
    private readonly ScriptRunnerOptions _o = options.Value;
    private readonly SemaphoreSlim _gate = new(options.Value.MaxSessions);

    public async Task RunAsync(WebSocket socket, CancellationToken ct)
    {
        var start = await ReceiveAsync(socket, ct);
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
        if (Text(start["type"]) != "start" || Text(start["bundle"]) is not { } bundle)
        {
            return Done("error", error: "The session must begin with a start message.");
        }
        if (bundle.Length > _o.MaxBundleChars)
        {
            return Done("limit", error: $"A function's bundle is at most {_o.MaxBundleChars:N0} characters.");
        }
        var limits = start["limits"] as JsonObject;
        var timeout = TimeSpan.FromMilliseconds(Clamp(limits?["timeoutMs"], 100, _o.MaxSessionTimeoutMs, 30_000));
        var heap = Clamp(limits?["heapMb"], 32, _o.MaxSessionHeapMegabytes, _o.DefaultSessionHeapMegabytes);

        await _gate.WaitAsync(ct);
        var cache = Directory.CreateTempSubdirectory("fn-");
        Process? process = null;
        try
        {
            // Deno's own start-up reserves most of the data limit (see DataLimitMegabytes); the
            // function's heap, and as much again off it for buffers, come on top.
            process = Confined.Start(_o.DenoPath,
                ["run", "--no-prompt", "--no-remote", "--no-npm", "--no-config", "--no-lock",
                 $"--v8-flags=--max-old-space-size={heap}", _o.SessionHarnessPath],
                (_o.DataLimitMegabytes + 2L * heap) * 1024 * 1024,
                new Dictionary<string, string> { ["DENO_DIR"] = cache.FullName });

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            var stderr = Confined.ReadBoundedAsync(process.StandardError, 4_000, onOverflow: null);
            var run = new Budget(process);

            try
            {
                await WriteLineAsync(process, new JsonObject
                {
                    ["type"] = "start",
                    ["mode"] = Text(start["mode"]) == "describe" ? "describe" : "run",
                    ["bundle"] = bundle,
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
                done = await RelayCallsAsync(socket, process, run, deadline.Token);
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
                return done;
            }
            await process.WaitForExitAsync(ct);
            var errors = await stderr;
            // 133: V8 aborted (SIGTRAP) at its heap limit; 137: the kernel's OOM killer.
            if (process.ExitCode is 133 or 137 || errors.Contains("heap limit", StringComparison.OrdinalIgnoreCase)
                || errors.Contains("out of memory", StringComparison.OrdinalIgnoreCase))
            {
                return Done("limit", error: $"The function used more than {heap} MB of memory.");
            }
            // The exit code only: stderr can quote the function and its data, and logs leave this box.
            logger.LogWarning("deno exited with {ExitCode} ({StderrChars} characters on stderr)", process.ExitCode, errors.Length);
            return Done("error", error: $"The function could not run (exit {process.ExitCode}).");
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

    /// <summary>Deno to the caller: host calls and log lines, until the function's done line or the end of its output.</summary>
    private async Task<JsonObject?> RelayCallsAsync(WebSocket socket, Process process, Budget run, CancellationToken ct)
    {
        var lines = new LineReader(process.StandardOutput, _o.MaxMessageBytes);
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
                        return Done("error", error: Text(message["error"]) ?? "The function failed.");
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
                    Confined.Kill(process);
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

    private async Task<Received> ReceiveAsync(WebSocket socket, CancellationToken ct)
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
            if (buffer.Length + read.Count > _o.MaxMessageBytes)
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
