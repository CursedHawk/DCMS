using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    public int Emails { get; set; }
    public int Notifications { get; set; }
}

/// <summary>
/// The broker (ADR 0029): answers one run's host calls. Every call is checked twice before it
/// does anything — the function declared it in <c>uses</c>, and the permission it needs is one
/// the deployer vouched for — and then runs here, in the caller's process and scope, through the
/// same contracts any plugin uses, so their owners audit what they do. The sandbox only asks.
/// </summary>
public sealed partial class HostCalls(
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
    public const int MaxFetchBodyBytes = 5 * 1024 * 1024;
    public const int MaxRedirects = 5;
    public const int DefaultFetchTimeoutMs = 10_000;
    public const int MaxFetchTimeoutMs = 30_000;
    public const int MaxEnqueues = 100;
    public const int MaxInvokes = 50;
    public const int MaxEmits = 100;
    public const int MaxMessageChars = 256 * 1024;
    public const int MaxEmails = 20;
    public const int MaxNotifications = 20;

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
                "email.send" => await EmailAsync(args, ct),
                "notifications.raise" => await NotifyAsync(args, ct),
                "content.get" or "content.list" => await ContentAsync(op, args, ct),
                "links.call" => await LinkedCallAsync(args, ct),
                "links.records" => await LinkedRecordsAsync(args, ct),
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
        Vouched(PluginPermissions.ForInstance(verb switch
        {
            "read" => FunctionManifest.DataRead,
            "delete" => FunctionManifest.DataDelete,
            _ => FunctionManifest.DataWrite,
        }, app));

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
            ContractPlane.Admin, Actor, permission => Task.FromResult(FunctionManifest.Covers(target.Vouched, permission)), ct);
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
        Vouched(Own(FunctionsPermissions.Secrets));
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
        Egress(url);
        Vouched(Own(FunctionsPermissions.Egress));
        var method = (args?["method"]?.GetValue<string>() ?? "GET").ToUpperInvariant();
        if (!Methods.Contains(method))
        {
            throw new HostCallException($"http.fetch does not send {method}.");
        }
        // The global fetch (and so ctx.http.fetch) sends bytes and wants bytes back; a bundle built
        // before it sends text and reads text, and follows no redirect: both still work as they did.
        var raw = args?["raw"]?.GetValue<bool>() == true;
        var redirect = args?["redirect"]?.GetValue<string>() ?? "manual";
        var timeoutMs = Math.Clamp(args?["timeoutMs"]?.GetValueKind() == JsonValueKind.Number ? args["timeoutMs"]!.GetValue<int>() : DefaultFetchTimeoutMs,
            100, MaxFetchTimeoutMs);
        byte[]? body = args?["bodyBase64"]?.GetValue<string>() is { } encoded ? Base64(encoded)
            : args?["body"]?.GetValue<string>() is { } plain ? Encoding.UTF8.GetBytes(plain) : null;
        if (body?.Length > MaxFetchBodyBytes)
        {
            throw new HostCallException($"A request body is at most {MaxFetchBodyBytes / (1024 * 1024)} MB.");
        }

        // {{dcms-secret:NAME}} (ctx.secrets.ref): the value goes in here, never into the sandbox.
        var headers = new List<(string Name, string Value, bool Secret)>();
        var substituted = new Dictionary<string, string>(StringComparer.Ordinal); // a value as sent → its ref
        foreach (var (name, value) in (args?["headers"] as JsonObject ?? []).Take(50))
        {
            if (value?.GetValueKind() != JsonValueKind.String || name.Equals("host", StringComparison.OrdinalIgnoreCase)) continue;
            var text = value.GetValue<string>();
            headers.Add((name, await WithSecretsAsync(text, escape: false, substituted, ct), SecretRef().IsMatch(text)));
        }
        url = new Uri(await WithSecretsAsync(url.OriginalString, escape: true, substituted, ct));

        for (var hop = 0; ; hop++)
        {
            Egress(url);
            if (++budget.Fetches > MaxFetches)
            {
                throw new HostCallException($"A run, with the functions it invokes, makes at most {MaxFetches} requests.");
            }
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (body is not null) request.Content = new ByteArrayContent(body);
            foreach (var (name, value, _) in headers)
            {
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    request.Content ??= new ByteArrayContent([]);
                    request.Content.Headers.Remove(name);
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try
            {
                using var response = await http.CreateClient(EgressClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location && redirect != "manual")
                {
                    if (redirect == "error") throw new HostCallException($"{url.IdnHost} redirected, and the request said not to follow.");
                    if (hop >= MaxRedirects) throw new HostCallException($"More than {MaxRedirects} redirects.");
                    var next = new Uri(url, location);
                    if (next.Scheme is not ("https" or "http")) throw new HostCallException("A redirect may only go to an http(s) URL.");
                    // As browsers do: 303, and a POST's 301/302, are followed with a GET and no body.
                    if (status == 303 || (status is 301 or 302 && method == "POST"))
                    {
                        method = method == "HEAD" ? "HEAD" : "GET";
                        body = null;
                        headers.RemoveAll(h => h.Name.StartsWith("content-", StringComparison.OrdinalIgnoreCase));
                    }
                    // Credentials stay with the origin they were meant for: the same scheme, host and
                    // port, so an https → http downgrade on the same host drops them too.
                    if (Uri.Compare(next, url, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) != 0)
                    {
                        headers.RemoveAll(h => h.Secret || h.Name.ToLowerInvariant() is "authorization" or "cookie" or "proxy-authorization");
                    }
                    url = next;
                    continue;
                }
                var bytes = await ReadBoundedAsync(response, timeout.Token);
                var answer = new JsonObject();
                foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                {
                    answer[name.ToLowerInvariant()] = Redact(string.Join(", ", values), substituted);
                }
                // What the function sees names a secret by its ref, never by its value: a URL that
                // carried one, or a Location that echoes it.
                var result = new JsonObject { ["status"] = status, ["headers"] = answer, ["url"] = Redact(url.AbsoluteUri, substituted) };
                if (raw) result["bodyBase64"] = Convert.ToBase64String(bytes);
                else result["body"] = Encoding.UTF8.GetString(bytes);
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HostCallException($"{url.IdnHost} did not answer within {timeoutMs / 1000.0:0.#} s.");
            }
            catch (HttpRequestException)
            {
                // Includes the egress handler refusing a private address: say no more than that.
                throw new HostCallException($"{url.IdnHost} could not be reached.");
            }
        }
    }

    /// <summary>Only the hosts the function declared, checked again on every redirect.</summary>
    private void Egress(Uri url)
    {
        var host = url.IdnHost.ToLowerInvariant();
        if (!Uses.Egress.Any(allowed => allowed == host || (allowed.StartsWith("*.") && host.EndsWith(allowed[1..]))))
        {
            throw new HostCallException($"This function does not declare uses.egress: [\"{host}\"].");
        }
    }

    /// <summary>Each <c>{{dcms-secret:NAME}}</c> replaced by the secret, for a secret the function declared.</summary>
    private async Task<string> WithSecretsAsync(string text, bool escape, Dictionary<string, string> substituted, CancellationToken ct)
    {
        var refs = SecretRef().Matches(text);
        if (refs.Count == 0) return text;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in refs.Select(m => m.Groups[1].Value).Distinct())
        {
            if (!Uses.Secrets.Contains(name))
            {
                throw new HostCallException($"This function does not declare uses.secrets: [\"{name}\"].");
            }
            Vouched(Own(FunctionsPermissions.Secrets));
            var secret = await context.Contracts.Get<IPluginSecrets>().GetAsync(new SecretName(name, target.Deployment.InstanceId), ct);
            var value = secret.Value ?? throw new HostCallException($"The secret {name} is not set.");
            if (value.Contains('\r') || value.Contains('\n'))
            {
                throw new HostCallException($"The secret {name} holds a line break and cannot go into a request.");
            }
            values[name] = escape ? Uri.EscapeDataString(value) : value;
            if (value.Length > 0)
            {
                substituted[value] = $"{{{{dcms-secret:{name}}}}}";
                substituted[Uri.EscapeDataString(value)] = $"{{{{dcms-secret:{name}}}}}";
            }
        }
        return SecretRef().Replace(text, m => values[m.Groups[1].Value]);
    }

    /// <summary>Each secret value the broker put into this request, back to its ref.</summary>
    private static string Redact(string text, Dictionary<string, string> substituted)
    {
        // Longest first, so a value that contains another is replaced whole.
        foreach (var (value, reference) in substituted.OrderByDescending(p => p.Key.Length))
        {
            text = text.Replace(value, reference, StringComparison.Ordinal);
        }
        return text;
    }

    private static byte[] Base64(string encoded)
    {
        try
        {
            return Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new HostCallException("http.fetch: the body is not base64.");
        }
    }

    [GeneratedRegex(@"(?:\{\{|%7B%7B)dcms-secret:([A-Z_][A-Z0-9_]{0,63})(?:\}\}|%7D%7D)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretRef();

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

    // ------------------------------------------------------------------ email.send / notifications.raise

    private async Task<JsonNode?> EmailAsync(JsonNode? args, CancellationToken ct)
    {
        if (!Uses.Email)
        {
            throw new HostCallException("This function does not declare uses.email: true.");
        }
        if (++budget.Emails > MaxEmails)
        {
            throw new HostCallException($"A run, with the functions it invokes, sends at most {MaxEmails} emails.");
        }
        var to = (args?["to"] switch
            {
                JsonArray list => list.Select(a => a is JsonValue v && v.TryGetValue<string>(out var t) ? t : ""),
                JsonValue one when one.TryGetValue<string>(out var t) => [t],
                _ => [],
            })
            .Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var subject = args?["subject"] is JsonValue sv && sv.TryGetValue<string>(out var sub) ? sub.Trim() : "";
        if (to.Count is 0 or > 20 || subject.Length is 0 or > 300)
        {
            throw new HostCallException("email.send needs 1 to 20 recipients and a subject of at most 300 characters.");
        }
        var text = args?["text"] is JsonValue tv && tv.TryGetValue<string>(out var t1) ? t1 : null;
        var markup = args?["html"] is JsonValue hv && hv.TryGetValue<string>(out var h1) ? h1 : null;
        if ((text?.Length ?? 0) + (markup?.Length ?? 0) > 256 * 1024)
        {
            throw new HostCallException("An email body is at most 256 KiB.");
        }
        // Mail in the workspace's name: its own markup is sanitized, plain text escaped.
        var html = markup is not null
            ? Dcms.Shared.Security.HtmlContentSanitizer.Sanitize(markup) ?? ""
            : System.Net.WebUtility.HtmlEncode(text ?? "").Replace("\n", "<br>", StringComparison.Ordinal);
        var replyTo = args?["replyTo"] is JsonValue rv && rv.TryGetValue<string>(out var r1) && r1.Length <= 320 ? r1 : null;
        // A retried run sends what it sent once: the key is the run and the email's place in it.
        var dedupe = trigger.RunId is { } run ? $"function:{run:N}:email:{budget.Emails}" : null;
        var queued = await context.Contracts.Get<IPluginEmail>().SendAsync(new EmailSend(to, subject, html, replyTo, dedupe), ct);
        return new JsonObject { ["recipients"] = queued.Recipients };
    }

    private async Task<JsonNode?> NotifyAsync(JsonNode? args, CancellationToken ct)
    {
        if (Uses.Notifications.Count == 0)
        {
            throw new HostCallException("This function does not declare uses.notifications: [\"<permission of those to tell>\"].");
        }
        var audience = args?["audience"] is JsonValue av && av.TryGetValue<string>(out var a) ? a : Uses.Notifications[0];
        if (!Uses.Notifications.Contains(audience))
        {
            throw new HostCallException($"This function does not declare uses.notifications: [\"{audience}\"].");
        }
        Vouched(audience);
        if (++budget.Notifications > MaxNotifications)
        {
            throw new HostCallException($"A run, with the functions it invokes, raises at most {MaxNotifications} notifications.");
        }
        string Text(string key, int max) => args?[key] is JsonValue v && v.TryGetValue<string>(out var x) && x.Trim().Length is > 0 and var n && n <= max
            ? x.Trim()
            : throw new HostCallException($"notifications.raise needs a {key} of 1 to {max} characters.");
        var link = args?["linkPath"] is JsonValue lv && lv.TryGetValue<string>(out var l) && l.StartsWith('/') && !l.StartsWith("//") && l.Length <= 300 ? l : null;
        var severity = (args?["severity"] as JsonValue)?.GetValue<string>() switch
        {
            "warning" => NotificationSeverity.Warning,
            "error" => NotificationSeverity.Error,
            _ => NotificationSeverity.Info,
        };
        var dedupe = trigger.RunId is { } run ? $"function:{run:N}:notification:{budget.Notifications}" : Guid.NewGuid().ToString("N");
        await context.Contracts.Get<IPluginNotifications>().RaiseAsync(new NotificationRaise(
            Text("title", 200), Text("body", 2000), audience, dedupe, severity, link ?? $"/apps/{context.Instance?.Slug}/runs"), ct);
        return null;
    }

    // ------------------------------------------------------------------ content.get / content.list

    private async Task<JsonNode?> ContentAsync(string op, JsonNode? args, CancellationToken ct)
    {
        if (!Uses.Content)
        {
            throw new HostCallException("This function does not declare uses.content: true.");
        }
        Vouched(Dcms.Shared.Security.PlatformPermissions.ContentRead);
        var type = args?["contentType"] is JsonValue tv && tv.TryGetValue<string>(out var t) && t.Length is > 0 and <= 100 ? t
            : throw new HostCallException($"{op} needs a contentType.");
        Guid? instance = args?["instanceId"] is JsonValue iv && iv.TryGetValue<string>(out var i) && Guid.TryParse(i, out var g) ? g : null;
        var content = context.Contracts.Get<IPluginContent>();
        if (op == "content.get")
        {
            var slug = args?["slug"] is JsonValue sv && sv.TryGetValue<string>(out var s) && s.Length is > 0 and <= 200 ? s
                : throw new HostCallException("content.get needs a slug.");
            return JsonSerializer.SerializeToNode(await content.GetBySlugAsync(new ContentLookup(type, slug, instance), ct), Json);
        }
        var page = args?["page"] is JsonValue pv && pv.TryGetValue<int>(out var p) ? Math.Max(1, p) : 1;
        var size = args?["pageSize"] is JsonValue zv && zv.TryGetValue<int>(out var z) ? Math.Clamp(z, 1, 50) : 20;
        return JsonSerializer.SerializeToNode(await content.ListAsync(new ContentListRequest(type, page, size, instance), ct), Json);
    }

    // ------------------------------------------------------------------ links.call / links.records(environment, app)

    /// <summary>
    /// Another environment's app, through a link that environment's managers set up (ADR 0028):
    /// run as that environment, and only what the link grants. The function names the app in
    /// <c>uses.links</c>; the link decides the rest.
    /// </summary>
    private (string Environment, string App) Link(JsonNode? args, string op)
    {
        var environment = args?["environment"] is JsonValue ev && ev.TryGetValue<string>(out var e) ? e : "";
        var app = args?["app"] is JsonValue av && av.TryGetValue<string>(out var a) ? a : "";
        return Uses.Links.Contains($"{environment}/{app}")
            ? (environment, app)
            : throw new HostCallException($"This function does not declare uses.links: [\"{environment}/{app}\"] ({op}).");
    }

    private async Task<JsonNode?> LinkedCallAsync(JsonNode? args, CancellationToken ct)
    {
        var (environment, app) = Link(args, "links.call");
        var contract = args?["contract"] is JsonValue cv && cv.TryGetValue<string>(out var c) ? c : "";
        var operation = args?["operation"] is JsonValue ov && ov.TryGetValue<string>(out var o) ? o : "";
        try
        {
            return await context.Contracts.LinkedCallAsync(environment, app, contract, operation, args?["input"]?.DeepClone(), ct);
        }
        catch (Exception e) when (e is ContractAccessDeniedException or ContractLimitException)
        {
            throw new HostCallException(e.Message);
        }
    }

    private async Task<JsonNode?> LinkedRecordsAsync(JsonNode? args, CancellationToken ct)
    {
        var (environment, app) = Link(args, "links.records");
        var table = args?["table"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : "";
        var a = args?["args"] as JsonArray ?? [];
        Guid Id(int i) => a.Count > i && a[i] is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g) ? g
            : throw new HostCallException("A record id is a uuid.");
        JsonObject Values(int i) => a.Count > i && a[i] is JsonObject o ? (JsonObject)o.DeepClone() : throw new HostCallException("Values are an object.");
        IDynamicAppsRecords records;
        try
        {
            records = await context.Contracts.LinkedAsync<IDynamicAppsRecords>(environment, app, ct);
            switch ((args?["method"] as JsonValue)?.GetValue<string>())
            {
                case "find":
                    var query = a.Count > 0 && a[0] is JsonObject q ? q : new JsonObject();
                    var page = await records.QueryRecordsAsync(new TableQuery(table, new RecordQuery
                    {
                        Filter = query["filter"]?.Deserialize<RecordFilter>(Json),
                        Sort = query["sort"]?.Deserialize<List<RecordSort>>(Json) ?? [],
                        PageSize = query["pageSize"] is JsonValue pv && pv.TryGetValue<int>(out var p) ? Math.Clamp(p, 1, 50) : 20,
                    }), ct);
                    return new JsonObject
                    {
                        ["items"] = JsonSerializer.SerializeToNode(page.Items, Json),
                        ["total"] = page.Total,
                        ["totalCapped"] = page.TotalCapped,
                    };
                case "get":
                    return (await records.GetRecordAsync(new RecordRequest(table, Id(0)), ct))?.DeepClone();
                case "create":
                    return await records.CreateRecordAsync(new RecordCreate(table, Values(0)), ct);
                case "update":
                    return await records.UpdateRecordAsync(new RecordUpdate(table, Id(0), Values(1)), ct);
                case "delete":
                    await records.DeleteRecordAsync(new RecordRequest(table, Id(0)), ct);
                    return null;
                default:
                    throw new HostCallException("links.records(…) has find, get, create, update and delete.");
            }
        }
        catch (Exception e) when (e is ContractAccessDeniedException or ContractLimitException)
        {
            throw new HostCallException(e.Message);
        }
    }

    // ------------------------------------------------------------------ helpers

    private PluginActor Actor => new(PluginActor.System.Kind, Key: $"function:{target.Function.Name}");

    /// <summary>One of this app's own permissions, on this app (plugin permissions are held per instance).</summary>
    private string Own(string permission) =>
        PluginPermissions.Resolve(FunctionsPlugin.PluginId, context.Instance?.Slug, permission);

    private void Vouched(string permission)
    {
        if (!FunctionManifest.Covers(target.Vouched, permission))
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
