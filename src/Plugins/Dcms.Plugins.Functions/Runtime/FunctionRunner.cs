using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.Functions.Build;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Rls;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Functions.Runtime;

/// <summary>Opens a session on script-runner's <c>/session</c>; tests swap in their in-process server.</summary>
public delegate Task<WebSocket> FunctionSocketFactory(CancellationToken ct);

/// <summary>What started a run: <c>http</c>, <c>manual</c>, <c>invoke</c>, <c>job</c>, and later the event kinds.</summary>
/// <param name="Generation">How many runs deep in a chain of enqueued jobs this is: 0 for one nothing enqueued.</param>
/// <param name="Origin">The action this run's chain began with; a new one when nothing set it off.</param>
/// <param name="RunId">The queued run, when it is one: what its writes and messages name as their cause.</param>
public sealed record RunTrigger(string Kind, JsonNode? Detail = null, int Generation = 0, RunOrigin? Origin = null, Guid? RunId = null)
{
    public RunOrigin Chain { get; } = Origin ?? RunOrigin.New();

    /// <summary>Requests wait for their answer; everything else is background work.</summary>
    public int MaxTimeoutMs => Kind is "http" or "manual" or "invoke" ? 30_000 : 300_000;
}

/// <summary>One function of one deployment, with the permissions it runs under (definer's rights).</summary>
/// <param name="Bundle">deno-ts: its bundle. dotnet: its class, run from <paramref name="Assembly"/> (ADR 0030).</param>
public sealed record RunTarget(FunctionDeployment Deployment, FunctionSpec Function, IReadOnlyCollection<string> Vouched, string Bundle,
    string? Assembly = null, string? Pdb = null, string? SourceMap = null);

/// <param name="Status"><c>ok</c>, <c>error</c> (the function threw or a call was refused) or <c>limit</c>.</param>
/// <param name="Trace">Its host calls in order (<see cref="RunTrace"/>): what the editor's Runs and Run tabs show.</param>
public sealed record RunOutcome(string Status, JsonNode? Result, string? Error, string Logs, int Calls, long DurationMs, JsonArray? Trace = null)
{
    public bool Succeeded => Status == "ok";
}

/// <summary>
/// Runs a function (ADR 0029): one session on script-runner, whose sandbox holds no credentials
/// and reaches nothing, and every host call it makes answered here by <see cref="HostCalls"/>, in
/// this process, inside the run's tenant and environment. Calls are answered one at a time, in
/// order: a run's calls share its scope and its budgets.
/// </summary>
public sealed class FunctionRunner(FunctionSocketFactory sockets, IServiceProvider services)
{
    public const int MaxLogChars = 64 * 1024;
    public const int MaxDepth = 5;
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    public async Task<RunOutcome> RunAsync(
        RunTarget target, JsonNode? input, RunTrigger trigger, CancellationToken ct,
        JsonObject? request = null, JsonObject? user = null, int depth = 0, RunBudget? budget = null, JsonObject? simulatedTrigger = null)
    {
        // Every call the run makes resolves in the deployment's own environment, however it was started.
        using var rls = RlsScope.Environment(target.Deployment.TenantId, target.Deployment.EnvironmentId);
        var clock = Stopwatch.StartNew();
        var timeout = Math.Min(target.Function.TimeoutMs ?? trigger.MaxTimeoutMs, trigger.MaxTimeoutMs);
        var calls = ActivatorUtilities.CreateInstance<HostCalls>(services, target, trigger, depth, budget ?? new RunBudget());
        var logs = new StringBuilder();
        var trace = new RunTrace();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // The runner enforces the limit; this only stops waiting on one that never answers.
        deadline.CancelAfter(timeout + 15_000);
        try
        {
            using var socket = await sockets(deadline.Token);
            var start = new JsonObject
            {
                ["type"] = "start",
                ["mode"] = "run",
                ["input"] = input?.DeepClone(),
                ["context"] = new JsonObject
                {
                    // What the function is told started it. A test from the editor may claim a row
                    // event or a message (workbench W2); the run itself stays what it is.
                    ["trigger"] = simulatedTrigger?.DeepClone() ?? new JsonObject { ["kind"] = trigger.Kind, ["detail"] = trigger.Detail?.DeepClone() },
                    ["run"] = new JsonObject { ["function"] = target.Function.Name, ["deployment"] = target.Deployment.Number, ["depth"] = depth },
                    ["request"] = request?.DeepClone(),
                    ["user"] = user?.DeepClone(),
                },
                ["limits"] = new JsonObject { ["timeoutMs"] = timeout, ["heapMb"] = target.Function.HeapMb ?? 128 },
            };
            if (target.Function.Runtime == FunctionManifest.Dotnet)
            {
                // C# (ADR 0030): the deployment's one assembly, and which class of it this function is.
                start["runtime"] = FunctionManifest.Dotnet;
                start["assembly"] = target.Assembly;
                start["pdb"] = target.Pdb;
                start["class"] = target.Bundle;
            }
            else
            {
                start["bundle"] = target.Bundle;
            }
            await SendAsync(socket, start, deadline.Token);

            while (true)
            {
                var message = await ReceiveAsync(socket, deadline.Token);
                switch (message?["type"]?.GetValue<string>())
                {
                    case "call":
                        var op = message["op"]?.GetValue<string>() ?? "";
                        var started = Stopwatch.GetTimestamp();
                        var reply = await calls.AnswerAsync(op, message["args"], deadline.Token);
                        trace.Add(op, message["args"], reply, Stopwatch.GetElapsedTime(started));
                        reply["type"] = "reply";
                        reply["id"] = message["id"]?.DeepClone();
                        await SendAsync(socket, reply, deadline.Token);
                        break;
                    case "log":
                        Append(logs, message["level"]?.GetValue<string>() ?? "log", message["message"]?.GetValue<string>() ?? "");
                        break;
                    case "done":
                        await CloseAsync(socket);
                        // A TypeScript error's frames point into the bundle: name the repository's files.
                        var error = message["error"]?.GetValue<string>() is { } raw ? SourceMaps.Map(raw, target.SourceMap) : null;
                        return new RunOutcome(
                            message["status"]?.GetValue<string>() ?? "error", message["result"]?.DeepClone(),
                            error, logs.ToString(), calls.Count, clock.ElapsedMilliseconds, trace.Entries);
                    case null:
                        return new RunOutcome("error", null, "The function's session ended without an answer.", logs.ToString(), calls.Count, clock.ElapsedMilliseconds, trace.Entries);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new RunOutcome("error", null, "The function runner could not be reached. Try again in a moment.",
                logs.ToString(), calls.Count, clock.ElapsedMilliseconds, trace.Entries);
        }
    }

    private static void Append(StringBuilder logs, string level, string line)
    {
        if (logs.Length >= MaxLogChars) return;
        var entry = $"[{level}] {line}\n";
        logs.Append(entry.Length <= MaxLogChars - logs.Length ? entry : entry[..(MaxLogChars - logs.Length)]);
    }

    private static Task SendAsync(WebSocket socket, JsonObject message, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonObject?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var text = new MemoryStream();
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer, ct);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            text.Write(buffer, 0, part.Count);
            if (text.Length > MaxMessageBytes) return null;
            if (part.EndOfMessage) break;
        }
        try
        {
            return JsonNode.Parse(text.ToArray()) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task CloseAsync(WebSocket socket)
    {
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, wait.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // The answer is in; how the socket ends is the runner's business.
        }
    }
}

/// <summary>
/// A run's host calls, as the editor shows them (workbench W3): op, arguments, ok or the error,
/// milliseconds. Recorded here, where every call passes, not in the sandbox. Never the results:
/// a secret's value or a record's contents are the reply, and stay out. What could carry a
/// credential in the arguments — an HTTP call's sensitive headers, its body, its query — is
/// redacted before it is kept. Bounded: 200 calls, each at most 300 characters.
/// </summary>
public sealed class RunTrace
{
    public const int MaxEntries = 200;
    private const int MaxArgsChars = 300;
    private int _omitted;

    private static readonly System.Text.Json.JsonSerializerOptions Readable =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public JsonArray Entries { get; } = [];

    public void Add(string op, JsonNode? args, JsonObject reply, TimeSpan elapsed)
    {
        if (Entries.Count >= MaxEntries)
        {
            // The marker takes the last entry's place, so that call is among the omitted too.
            _omitted += _omitted == 0 ? 2 : 1;
            Entries[^1] = new JsonObject { ["op"] = "…", ["args"] = $"{_omitted} more calls", ["ok"] = true, ["ms"] = 0 };
            return;
        }
        // Shown as text, never as markup: no need to escape what is not ASCII.
        var text = Redact(op, args?.DeepClone())?.ToJsonString(Readable) ?? "null";
        Entries.Add(new JsonObject
        {
            ["op"] = op,
            ["args"] = text.Length > MaxArgsChars ? text[..MaxArgsChars] + "…" : text,
            ["ok"] = reply["ok"]?.GetValue<bool>() == true,
            ["error"] = reply["error"]?.DeepClone(),
            ["ms"] = (long)elapsed.TotalMilliseconds,
        });
    }

    private static JsonNode? Redact(string op, JsonNode? args)
    {
        if (op != "http.fetch" || args is not JsonObject call) return args;
        if (call["headers"] is JsonObject headers)
        {
            foreach (var name in headers.Select(h => h.Key).ToList())
            {
                var lower = name.ToLowerInvariant();
                if (lower is "authorization" or "proxy-authorization" or "cookie"
                    || lower.Contains("token", StringComparison.Ordinal) || lower.Contains("secret", StringComparison.Ordinal)
                    || lower.Contains("key", StringComparison.Ordinal) || lower.Contains("signature", StringComparison.Ordinal))
                {
                    headers[name] = "•••";
                }
            }
        }
        if (call["url"]?.GetValue<string>() is { } url && url.IndexOf('?') is var q and >= 0)
        {
            call["url"] = url[..q] + "?…";
        }
        if (call["body"]?.GetValue<string>() is { } body)
        {
            call["body"] = $"({body.Length} characters)";
        }
        return call;
    }
}

/// <summary>A refused or failed host call, as the function sees it: an Error with this message.</summary>
public sealed class HostCallException(string message) : Exception(message);

/// <summary>Finds what a run executes: the instance's live deployment, or a built one being tried out.</summary>
public static class RunTargets
{
    public static RunTarget? Of(FunctionDeployment deployment, string function, IReadOnlyCollection<string> vouched)
    {
        var spec = FunctionManifest.Deserialize(deployment.Manifest).FirstOrDefault(f => f.Name == function);
        var bundles = JsonSerializer.Deserialize<Dictionary<string, string>>(deployment.Bundles) ?? [];
        return spec is not null && bundles.TryGetValue(function, out var bundle)
            ? new RunTarget(deployment, spec, vouched, bundle,
                bundles.GetValueOrDefault(BuildOutput.DotnetAssembly), bundles.GetValueOrDefault(BuildOutput.DotnetPdb),
                bundles.GetValueOrDefault(function + ".map"))
            : null;
    }

    /// <summary>Thrown for an input the caller got wrong.</summary>
    public static ContractValidationException NoSuchFunction(string name) => new($"There is no function '{name}' in this deployment.");
}
