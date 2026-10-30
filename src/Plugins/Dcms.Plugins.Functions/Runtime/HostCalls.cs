using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.Functions.Api;
using Dcms.Plugins.Functions.Build;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.Functions.Runtime;

/// <summary>What a run and every function it invokes share: one tree, one set of caps.</summary>
public sealed class RunBudget
{
    public int Fetches { get; set; }
    public int Enqueues { get; set; }
    public int Invokes { get; set; }
    public int Emits { get; set; }
}

/// <summary>
/// The broker (ADR 0029): answers one run's host calls. Every call is checked twice before it
/// does anything — the function declared it in <c>uses</c>, and the permission it needs is one
/// the deployer vouched for — and then runs here, in the caller's process and scope, through the
/// same contracts any plugin uses, so their owners audit what they do. The sandbox only asks.
/// </summary>
public sealed class HostCalls(
    RunTarget target,
    RunTrigger trigger,
    int depth,
    RunBudget budget,
    IPluginContext context,
    PluginRegistry registry,
    ContractDispatcher dispatcher,
    PluginContextFactory instances,
    IHttpClientFactory http,
    IServiceProvider services,
    ILogger<HostCalls> logger)
{
    public const string EgressClientName = "functions-egress";
    public const int MaxFetches = 50;
    public const int MaxFetchBytes = 5 * 1024 * 1024;
    public const int MaxFetchBodyChars = 1024 * 1024;
    public const int MaxEnqueues = 100;
    public const int MaxInvokes = 50;
    public const int MaxEmits = 100;
    public const int MaxMessageChars = 256 * 1024;

    /// <summary>A job that enqueues a job that enqueues…: how long such a chain may grow.</summary>
    public const int MaxGenerations = 10;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];


    private FunctionUses Uses => target.Function.Uses;

    public int Count { get; private set; }

    public async Task<JsonObject> AnswerAsync(string op, JsonNode? args, CancellationToken ct)
    {
        Count++;
        try
        {
            var result = op switch
            {
                "db" => await DbAsync(args, ct),
                "contract" => await ContractAsync(args, ct),
                "secrets.get" => await SecretAsync(args, ct),
                "http.fetch" => await FetchAsync(args, ct),
                "functions.invoke" => await InvokeAsync(args, ct),
                "jobs.enqueue" => await EnqueueAsync(args, ct),
                "messages.emit" => await EmitAsync(args, ct),
                _ => throw new HostCallException($"'{op}' is not something a function can do here."),
            };
            return new JsonObject { ["ok"] = true, ["result"] = result };
        }
        catch (Exception e) when (e is HostCallException or ContractValidationException or ContractConflictException
                                      or JsonException or FormatException or InvalidOperationException)
        {
            // What the function did wrong, in its own terms; never a stack or a host's internals.
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Host call {Op} of function {Function} failed.", op, target.Function.Name);
            return new JsonObject { ["ok"] = false, ["error"] = $"'{op}' failed. Try again; if it keeps failing, it is not your code." };
        }
    }

    // ------------------------------------------------------------------ db.<app>.<table>.<method>(...)

    private async Task<JsonNode?> DbAsync(JsonNode? args, CancellationToken ct)
    {
        var (path, a) = PathAndArgs(args, 3, "db.<app>.<table>.<method>(...)");
        var (app, table, method) = (path[0], path[1], path[2]);
        var verb = method switch
        {
            "find" or "get" or "count" => "read",
            "create" => "create",
            "update" or "bulkUpdate" or "link" or "unlink" => "update",
            "delete" or "bulkDelete" => "delete",
            _ => throw new HostCallException($"db.{app}.{table} has no method '{method}'."),
        };
        var key = $"{app}.{table}";
        if (!Uses.Tables.TryGetValue(key, out var verbs) || !verbs.Contains(verb))
        {
            throw new HostCallException($"This function does not declare uses.tables[\"{key}\"]: [\"{verb}\"].");
        }
        Vouched(verb switch
        {
            "read" => "plugin:dynamic-apps:data-read",
            "delete" => "plugin:dynamic-apps:data-delete",
            _ => "plugin:dynamic-apps:data-write",
        });

        var provider = (await instances.EnabledInstancesAsync(context.TenantId, ct))
            .FirstOrDefault(i => i.Slug == app && i.PluginId == "dynamic-apps")
            ?? throw new HostCallException($"There is no Dynamic Apps app '{app}' here.");
        var records = context.Contracts.Get<IDynamicAppsAutomation>(provider.InstanceId);
        // What this writes is this run's doing: the events it raises cascade from it, counted.
        using var cause = AutomationCause.Enter(new AutomationCause.Cause(
            trigger.Chain.CorrelationId, trigger.RunId ?? trigger.Chain.CorrelationId, trigger.Chain.Depth));

        // A read may follow a lookup into another table (expand, or a filter or sort on
        // company.name): that table must be declared readable too.
        var query = method == "find" ? Arg<RecordQuery>(a, 0) ?? new RecordQuery() : null;
        var filter = method == "count" ? Arg<RecordFilter>(a, 0) : query?.Filter;
        var expand = method == "get" ? Arg<GetOptions>(a, 1)?.Expand ?? [] : query?.Expand ?? [];
        var lookups = expand
            .Concat(Fields(filter).Concat(query?.Sort.Select(s => s.Field) ?? []).Concat(query?.Select ?? [])
                .Where(f => f.Contains('.')).Select(f => f[..f.IndexOf('.')]))
            .Distinct().ToList();
        if (lookups.Count > 0)
        {
            await ReadableThroughAsync(records, app, table, lookups, ct);
        }

        return method switch
        {
            "find" => Node(await records.QueryAsync(new TableQuery(table, query), ct)),
            "get" => await records.GetAsync(new RecordRequest(table, Id(a, 0), expand), ct),
            "count" => (await records.QueryAsync(new TableQuery(table, new RecordQuery { Filter = filter, PageSize = 1 }), ct)).Total,
            "create" => await records.CreateAsync(new RecordCreate(table, Values(a, 0)), ct),
            "update" => await records.UpdateAsync(new RecordUpdate(table, Id(a, 0), Values(a, 1)), ct),
            "delete" => await records.DeleteAsync(new RecordRequest(table, Id(a, 0)), ct),
            "link" => await records.LinkAsync(new RecordLink(table, Id(a, 0), Text(a, 1), Id(a, 2)), ct),
            "unlink" => await records.UnlinkAsync(new RecordLink(table, Id(a, 0), Text(a, 1), Id(a, 2)), ct),
            "bulkUpdate" => Node(await records.BulkUpdateAsync(new RecordsUpdate(table, Arg<List<Guid>>(a, 0) ?? [], Values(a, 1)), ct)),
            _ => Node(await records.BulkDeleteAsync(new RecordsDelete(table, Arg<List<Guid>>(a, 0) ?? []), ct)),
        };
    }

    private sealed record GetOptions(List<string>? Expand);

    private async Task ReadableThroughAsync(IDynamicAppsAutomation records, string app, string table, IReadOnlyList<string> lookups, CancellationToken ct)
    {
        var model = await records.ModelAsync(ct) ?? throw new HostCallException($"The app '{app}' has nothing published.");
        var source = model.Tables.FirstOrDefault(t => t.ApiName == table) ?? throw new HostCallException($"The app '{app}' has no table '{table}'.");
        foreach (var lookup in lookups)
        {
            var target = model.Relationships.FirstOrDefault(r => r.SourceTableId == source.Id && r.ApiName == lookup) is { } relationship
                ? model.Tables.FirstOrDefault(t => t.Id == relationship.TargetTableId)
                : null;
            if (target is null)
            {
                throw new HostCallException($"{table} has no lookup '{lookup}'.");
            }
            if (!Uses.Tables.TryGetValue($"{app}.{target.ApiName}", out var verbs) || !verbs.Contains("read"))
            {
                throw new HostCallException($"Following {table}.{lookup} reads {target.ApiName}: declare uses.tables[\"{app}.{target.ApiName}\"]: [\"read\"].");
            }
        }
    }

    private static IEnumerable<string> Fields(RecordFilter? filter) =>
        filter is null
            ? []
            : (filter.Field is { } field ? [field] : Enumerable.Empty<string>())
                .Concat((filter.And ?? []).Concat(filter.Or ?? []).Append(filter.Not).SelectMany(Fields));

    // ------------------------------------------------------------------ plugins.<instance>.<contract>.<operation>(input)

    private async Task<JsonNode?> ContractAsync(JsonNode? args, CancellationToken ct)
    {
        var (path, a) = PathAndArgs(args, 3, "plugins.<instance>.<contract>.<operation>(input)");
        // forms.submissions, formsSubmissions or forms-submissions: the same contract.
        var contract = registry.Contracts.Select(c => c.Descriptor)
            .FirstOrDefault(d => !d.IsPlatform && Loose(d.Name) == Loose(path[1]))
            ?? throw new HostCallException($"There is no contract '{path[1]}' a function can call.");
        if (!Uses.Contracts.Contains(contract.Id))
        {
            throw new HostCallException($"This function does not declare uses.contracts: [\"{contract.Id}\"].");
        }
        var operation = contract.Operations.FirstOrDefault(o => Loose(o.Name) == Loose(path[2]))
            ?? throw new HostCallException($"{contract.Id} has no operation '{path[2]}'.");

        // The admin plane, as a member would call it, with the deployer's vouched permissions.
        var input = a.Count > 0 && a[0] is { } given ? JsonSerializer.SerializeToElement(given) : (JsonElement?)null;
        var outcome = await dispatcher.ExecuteAsync(context.TenantId, contract.Id, operation.Name, path[0], input,
            ContractPlane.Admin, Actor, permission => Task.FromResult(target.Vouched.Contains(permission)), ct);
        return outcome.Status switch
        {
            200 => JsonSerializer.SerializeToNode(outcome.Value, ContractDescriptorBuilder.Json),
            204 => null,
            404 => throw new HostCallException($"plugins.{path[0]}.{path[1]}.{path[2]} is not available here."),
            _ => throw new HostCallException(outcome.Error ?? $"plugins.{path[0]}.{path[1]}.{path[2]} failed ({outcome.Status})."),
        };
    }

    /// <summary>The permission set a contract's admin operations need, for vouching at deploy time.</summary>
    public static IEnumerable<string> ContractPermissions(PluginRegistry registry, string declared)
    {
        var descriptor = registry.FindContract(declared)?.Descriptor;
        return descriptor is null || descriptor.IsPlatform
            ? []
            : descriptor.Operations.Where(o => o.Expose.HasFlag(OpExposure.Admin) && o.Permission is not null).Select(o => o.Permission!);
    }

    // ------------------------------------------------------------------ secrets.get(name)

    private async Task<JsonNode?> SecretAsync(JsonNode? args, CancellationToken ct)
    {
        var name = args?["name"]?.GetValue<string>() ?? "";
        if (!Uses.Secrets.Contains(name))
        {
            throw new HostCallException($"This function does not declare uses.secrets: [\"{name}\"].");
        }
        Vouched(FunctionsPermissions.Secrets);
        var secret = await context.Contracts.Get<IPluginSecrets>().GetAsync(new SecretName(name, target.Deployment.InstanceId), ct);
        return secret.Value;
    }

    // ------------------------------------------------------------------ http.fetch(url, init)

    private async Task<JsonNode?> FetchAsync(JsonNode? args, CancellationToken ct)
    {
        if (!Uri.TryCreate(args?["url"]?.GetValue<string>(), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
        {
            throw new HostCallException("http.fetch takes an absolute http(s) URL.");
        }
        var host = url.IdnHost.ToLowerInvariant();
        if (!Uses.Egress.Any(allowed => allowed == host || (allowed.StartsWith("*.") && host.EndsWith(allowed[1..]))))
        {
            throw new HostCallException($"This function does not declare uses.egress: [\"{host}\"].");
        }
        if (++budget.Fetches > MaxFetches)
        {
            throw new HostCallException($"A run, with the functions it invokes, makes at most {MaxFetches} requests.");
        }
        var method = (args?["method"]?.GetValue<string>() ?? "GET").ToUpperInvariant();
        if (!Methods.Contains(method))
        {
            throw new HostCallException($"http.fetch does not send {method}.");
        }
        var body = args?["body"]?.GetValue<string>();
        if (body?.Length > MaxFetchBodyChars)
        {
            throw new HostCallException($"A request body is at most {MaxFetchBodyChars / 1024} KiB.");
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8);
        if (args?["headers"] is JsonObject headers)
        {
            foreach (var (name, value) in headers.Take(50))
            {
                if (value?.GetValueKind() != JsonValueKind.String || name.Equals("host", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(name, value.GetValue<string>()))
                {
                    request.Content ??= new StringContent("");
                    request.Content.Headers.Remove(name);
                    request.Content.Headers.TryAddWithoutValidation(name, value.GetValue<string>());
                }
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await http.CreateClient(EgressClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var bytes = await ReadBoundedAsync(response, timeout.Token);
            var answer = new JsonObject();
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                answer[name.ToLowerInvariant()] = string.Join(", ", values);
            }
            return new JsonObject { ["status"] = (int)response.StatusCode, ["headers"] = answer, ["body"] = Encoding.UTF8.GetString(bytes) };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HostCallException($"{host} did not answer within 10 s.");
        }
        catch (HttpRequestException)
        {
            // Includes the egress handler refusing a private address: say no more than that.
            throw new HostCallException($"{host} could not be reached.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxFetchBytes)
            {
                throw new HostCallException($"A response is at most {MaxFetchBytes / (1024 * 1024)} MB.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // ------------------------------------------------------------------ functions.invoke / jobs.enqueue

    private async Task<JsonNode?> InvokeAsync(JsonNode? args, CancellationToken ct)
    {
        var name = args?["name"]?.GetValue<string>() ?? "";
        if (depth + 1 >= FunctionRunner.MaxDepth)
        {
            throw new HostCallException($"Functions call each other at most {FunctionRunner.MaxDepth} deep.");
        }
        if (++budget.Invokes > MaxInvokes)
        {
            throw new HostCallException($"A run, with the functions it invokes, invokes at most {MaxInvokes} functions.");
        }
        var callee = RunTargets.Of(target.Deployment, name, target.Vouched)
                     ?? throw new HostCallException($"There is no function '{name}' in this deployment.");
        var outcome = await services.GetRequiredService<FunctionRunner>()
            .RunAsync(callee, args?["input"], trigger with { Kind = "invoke", Detail = null }, ct, depth: depth + 1, budget: budget);
        return outcome.Succeeded ? outcome.Result : throw new HostCallException($"{name}: {outcome.Error}");
    }

    private async Task<JsonNode?> EnqueueAsync(JsonNode? args, CancellationToken ct)
    {
        var name = args?["function"]?.GetValue<string>() ?? "";
        if (RunTargets.Of(target.Deployment, name, target.Vouched) is null)
        {
            throw new HostCallException($"There is no function '{name}' in this deployment.");
        }
        if (trigger.Generation + 1 > MaxGenerations)
        {
            throw new HostCallException($"Jobs enqueued by jobs chain at most {MaxGenerations} deep.");
        }
        if (++budget.Enqueues > MaxEnqueues)
        {
            throw new HostCallException($"A run, with the functions it invokes, enqueues at most {MaxEnqueues} jobs.");
        }
        var dedupe = args?["options"]?["dedupeKey"]?.GetValue<string>();
        if (dedupe?.Length > 200)
        {
            throw new HostCallException("A dedupeKey is at most 200 characters.");
        }
        var ticket = await RunJob.EnqueueAsync(context, target.Deployment.InstanceId, name, args?["payload"], dedupe, trigger.Generation + 1, ct);
        return new JsonObject { ["id"] = ticket.JobId };
    }

    // ------------------------------------------------------------------ messages.emit(topic, payload)

    /// <summary>
    /// Starts every function on the topic in every live deployment of this environment. The
    /// message carries this run's chain one level deeper, so a function that answers its own
    /// messages stops at the cascade limits. A retried run emits the same messages again under
    /// the same ids, which start nothing twice.
    /// </summary>
    private async Task<JsonNode?> EmitAsync(JsonNode? args, CancellationToken ct)
    {
        var topic = args?["topic"]?.GetValue<string>() ?? "";
        if (!Uses.Emits.Contains(topic))
        {
            throw new HostCallException($"This function does not declare uses.messages.emit: [\"{topic}\"].");
        }
        var payload = args?["payload"];
        if (payload?.ToJsonString().Length > MaxMessageChars)
        {
            throw new HostCallException($"A message is at most {MaxMessageChars / 1024} KiB.");
        }
        var index = ++budget.Emits;
        if (index > MaxEmits)
        {
            throw new HostCallException($"A run, with the functions it invokes, emits at most {MaxEmits} messages.");
        }
        var queue = new RunQueue(services.GetRequiredService<Dcms.Shared.Data.Functions.FunctionsDbContext>());
        var eventId = trigger.RunId is { } run ? RunQueue.EventId($"message:{run:N}:{index}") : Guid.CreateVersion7();
        var started = 0;
        foreach (var (live, functions) in await queue.OnAsync(t => t["kind"]?.GetValue<string>() == "message" && t["topic"]?.GetValue<string>() == topic, ct))
        {
            foreach (var function in functions)
            {
                if (await queue.EnqueueAsync(live, function.Name, "message", eventId, payload?.DeepClone(),
                        new JsonObject { ["topic"] = topic, ["from"] = target.Function.Name },
                        new RunOrigin(trigger.Chain.CorrelationId, trigger.RunId, trigger.Chain.Depth + 1), ct))
                {
                    started++;
                }
            }
        }
        return new JsonObject { ["started"] = started };
    }

    // ------------------------------------------------------------------ helpers

    private PluginActor Actor => new(PluginActor.System.Kind, Key: $"function:{target.Function.Name}");

    private void Vouched(string permission)
    {
        if (!target.Vouched.Contains(permission))
        {
            throw new HostCallException($"Deployment {target.Deployment.Number} was not deployed by someone holding '{permission}'. Deploy it again.");
        }
    }

    private static string Loose(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static (string[] Path, JsonArray Args) PathAndArgs(JsonNode? args, int length, string shape)
    {
        var path = (args?["path"] as JsonArray)?.Select(p => p?.GetValue<string>() ?? "").ToArray() ?? [];
        if (path.Length != length || path.Any(string.IsNullOrEmpty))
        {
            throw new HostCallException($"Call it as {shape}.");
        }
        return (path, args?["args"] as JsonArray ?? []);
    }

    private static T? Arg<T>(JsonArray a, int i) => a.Count > i && a[i] is { } node ? node.Deserialize<T>(Json) : default;

    private static Guid Id(JsonArray a, int i) =>
        a.Count > i && a[i]?.GetValueKind() == JsonValueKind.String && Guid.TryParse(a[i]!.GetValue<string>(), out var id)
            ? id
            : throw new HostCallException($"Argument {i + 1} must be a record id.");

    private static string Text(JsonArray a, int i) =>
        a.Count > i && a[i]?.GetValueKind() == JsonValueKind.String ? a[i]!.GetValue<string>() : throw new HostCallException($"Argument {i + 1} must be a string.");

    private static JsonObject Values(JsonArray a, int i) =>
        a.Count > i && a[i] is JsonObject values ? (JsonObject)values.DeepClone() : throw new HostCallException($"Argument {i + 1} must be an object of field values.");

    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json);
}
