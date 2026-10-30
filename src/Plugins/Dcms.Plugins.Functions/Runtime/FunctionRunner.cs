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
public sealed record RunTrigger(string Kind, JsonNode? Detail = null, int Generation = 0)
{
    /// <summary>Requests wait for their answer; everything else is background work.</summary>
    public int MaxTimeoutMs => Kind is "http" or "manual" or "invoke" ? 30_000 : 300_000;
}

/// <summary>One function of one deployment, with the permissions it runs under (definer's rights).</summary>
public sealed record RunTarget(FunctionDeployment Deployment, FunctionSpec Function, IReadOnlyCollection<string> Vouched, string Bundle);

/// <param name="Status"><c>ok</c>, <c>error</c> (the function threw or a call was refused) or <c>limit</c>.</param>
public sealed record RunOutcome(string Status, JsonNode? Result, string? Error, string Logs, int Calls, long DurationMs)
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
        JsonObject? request = null, JsonObject? user = null, int depth = 0, RunBudget? budget = null)
    {
        // Every call the run makes resolves in the deployment's own environment, however it was started.
        using var rls = RlsScope.Environment(target.Deployment.TenantId, target.Deployment.EnvironmentId);
        var clock = Stopwatch.StartNew();
        var timeout = Math.Min(target.Function.TimeoutMs ?? trigger.MaxTimeoutMs, trigger.MaxTimeoutMs);
        var calls = ActivatorUtilities.CreateInstance<HostCalls>(services, target, trigger, depth, budget ?? new RunBudget());
        var logs = new StringBuilder();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // The runner enforces the limit; this only stops waiting on one that never answers.
        deadline.CancelAfter(timeout + 15_000);
        try
        {
            using var socket = await sockets(deadline.Token);
            await SendAsync(socket, new JsonObject
            {
                ["type"] = "start",
                ["mode"] = "run",
                ["bundle"] = target.Bundle,
                ["input"] = input?.DeepClone(),
                ["context"] = new JsonObject
                {
                    ["trigger"] = new JsonObject { ["kind"] = trigger.Kind, ["detail"] = trigger.Detail?.DeepClone() },
                    ["run"] = new JsonObject { ["function"] = target.Function.Name, ["deployment"] = target.Deployment.Number, ["depth"] = depth },
                    ["request"] = request?.DeepClone(),
                    ["user"] = user?.DeepClone(),
                },
                ["limits"] = new JsonObject { ["timeoutMs"] = timeout, ["heapMb"] = target.Function.HeapMb ?? 128 },
            }, deadline.Token);

            while (true)
            {
                var message = await ReceiveAsync(socket, deadline.Token);
                switch (message?["type"]?.GetValue<string>())
                {
                    case "call":
                        var reply = await calls.AnswerAsync(message["op"]?.GetValue<string>() ?? "", message["args"], deadline.Token);
                        reply["type"] = "reply";
                        reply["id"] = message["id"]?.DeepClone();
                        await SendAsync(socket, reply, deadline.Token);
                        break;
                    case "log":
                        Append(logs, message["level"]?.GetValue<string>() ?? "log", message["message"]?.GetValue<string>() ?? "");
                        break;
                    case "done":
                        await CloseAsync(socket);
                        return new RunOutcome(
                            message["status"]?.GetValue<string>() ?? "error", message["result"]?.DeepClone(),
                            message["error"]?.GetValue<string>(), logs.ToString(), calls.Count, clock.ElapsedMilliseconds);
                    case null:
                        return new RunOutcome("error", null, "The function's session ended without an answer.", logs.ToString(), calls.Count, clock.ElapsedMilliseconds);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new RunOutcome("error", null, "The function runner could not be reached. Try again in a moment.",
                logs.ToString(), calls.Count, clock.ElapsedMilliseconds);
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

/// <summary>A refused or failed host call, as the function sees it: an Error with this message.</summary>
public sealed class HostCallException(string message) : Exception(message);

/// <summary>Finds what a run executes: the instance's live deployment, or a built one being tried out.</summary>
public static class RunTargets
{
    public static RunTarget? Of(FunctionDeployment deployment, string function, IReadOnlyCollection<string> vouched)
    {
        var spec = FunctionManifest.Deserialize(deployment.Manifest).FirstOrDefault(f => f.Name == function);
        var bundles = JsonSerializer.Deserialize<Dictionary<string, string>>(deployment.Bundles) ?? [];
        return spec is not null && bundles.TryGetValue(function, out var bundle) ? new RunTarget(deployment, spec, vouched, bundle) : null;
    }

    /// <summary>Thrown for an input the caller got wrong.</summary>
    public static ContractValidationException NoSuchFunction(string name) => new($"There is no function '{name}' in this deployment.");
}
