using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>A step failed in a way retrying will not fix: a bad input, a missing record, or a limit (<paramref name="limit"/>).</summary>
public sealed class FlowFatalException(string message, bool limit = false) : Exception(message)
{
    /// <summary>A cascade or resource limit stopped the run; it ends terminated rather than failed.</summary>
    public bool Limit { get; } = limit;
}

/// <summary>What a running step can reach: its run, its plugin context and the scope's services.</summary>
public sealed class FlowActionContext(IServiceProvider services, IPluginContext plugin, FlowRun run, string stepId)
{
    public IServiceProvider Services { get; } = services;
    public IPluginContext Plugin { get; } = plugin;
    public FlowRun Run { get; } = run;
    public string StepId { get; } = stepId;

    /// <summary>Stable across retries of this step: what a side effect deduplicates on.</summary>
    public string IdempotencyKey => $"{Run.Id}:{StepId}";

    /// <exception cref="FlowFatalException">The run has written as many records as it may.</exception>
    public void CountWrite()
    {
        if (++Run.Writes > AutomationLimits.MaxWrites)
        {
            throw new FlowFatalException($"The run reached its limit of {AutomationLimits.MaxWrites} record writes.", limit: true);
        }
    }
}

/// <summary>
/// One thing a flow step can do. Identified by <c>{Id}@{Major}</c>; a published flow names a
/// major version, and an action's meaning never changes within one — a change of meaning is a
/// new major alongside the old (ADR 0021).
/// </summary>
public interface IFlowAction
{
    string Id { get; }
    int Major { get; }
    string Description { get; }

    /// <summary>JSON Schema of the input, for the designer and the assistant.</summary>
    JsonObject InputSchema { get; }

    OpRisk Risk { get; }

    /// <summary>Inputs that are HTML: template holes in them are HTML-encoded before the action sees them.</summary>
    IReadOnlySet<string> HtmlInputs => NoInputs;

    static readonly IReadOnlySet<string> NoInputs = new HashSet<string>();

    Task<JsonNode?> RunAsync(FlowActionContext context, JsonObject input, CancellationToken ct);
}

/// <summary>The actions a flow may call, by <c>id@major</c>.</summary>
public static partial class ActionCatalog
{
    public static readonly IReadOnlyList<IFlowAction> All =
    [
        new RecordsCreateAction(), new RecordsUpdateAction(), new RecordsDeleteAction(), new RecordsLookupAction(), new RecordsQueryAction(),
        new FlowInvokeAction(), new EventPublishAction(),
        new EmailSendAction(), new NotificationRaiseAction(),
        new ContentGetAction(), new ContentListAction(),
        new VisitorLookupAction(),
    ];

    private static readonly Dictionary<string, IFlowAction> ByKey = All.ToDictionary(a => Key(a), StringComparer.Ordinal);

    public static IReadOnlySet<string> Keys { get; } = ByKey.Keys.ToHashSet(StringComparer.Ordinal);

    public static string Key(IFlowAction action) => $"{action.Id}@{action.Major}";

    public static IFlowAction? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>
    /// The built-in actions and every action an enabled <c>automation.actions@1</c> provider
    /// offers this tenant. A provider's action never shadows a built-in one, and of two providers
    /// offering the same key the first wins.
    /// </summary>
    public static async Task<IReadOnlyList<IFlowAction>> WithProvidersAsync(IPluginContext context, CancellationToken ct)
    {
        var all = new List<IFlowAction>(All);
        var keys = Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var provider in context.Contracts.GetAll<IAutomationActionProvider>())
        {
            // A plugin offers actions in its own name only, so none can pass itself off as
            // another's (and dodge the permission that one asks for).
            if (provider is not ContractProxy { ProviderPluginId: var owner })
            {
                continue;
            }
            AutomationActionList offers;
            try
            {
                offers = await provider.ListAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One broken provider must not take the designer and every publish down with it;
                // a flow using its actions fails validation as naming an unknown action instead.
                continue;
            }
            foreach (var offered in offers.Actions.Take(MaxActionsPerProvider))
            {
                var action = new ProvidedAction(provider, offered);
                if (ProvidedName().IsMatch(offered.Name) && offered.Name.StartsWith(owner + ".", StringComparison.Ordinal)
                    && offered.Major >= 1 && keys.Add(Key(action)))
                {
                    all.Add(action);
                }
            }
        }
        return all;
    }

    /// <summary>A built-in action by key, or else a provider's.</summary>
    public static async Task<IFlowAction?> FindAsync(IPluginContext context, string key, CancellationToken ct) =>
        Find(key) ?? (await WithProvidersAsync(context, ct)).FirstOrDefault(a => Key(a) == key);

    /// <summary>
    /// For each flow, the permissions its actions need (<see cref="ProvidedAction.Permission"/>):
    /// its own steps', and those of every flow it can set off — by <c>flow.invoke</c>, by
    /// <c>event.publish</c> or by writing a table a row trigger watches — however deep. Whoever
    /// starts or changes a flow can steer everything downstream of it.
    /// </summary>
    public static Dictionary<string, HashSet<string>> PermissionsByFlow(AppConfig config, IReadOnlyDictionary<string, IFlowAction> actions)
    {
        var needs = config.Flows.DistinctBy(f => f.ApiName).ToDictionary(f => f.ApiName, f => f.Steps
            .Select(s => actions.GetValueOrDefault(s.Action)).OfType<ProvidedAction>()
            .Select(a => a.Permission).OfType<string>().ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var starts = config.Flows.DistinctBy(f => f.ApiName).ToDictionary(f => f.ApiName, f => Starts(config, f).ToList(), StringComparer.Ordinal);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (flow, targets) in starts)
            {
                foreach (var permission in targets.SelectMany(t => needs[t]).ToList())
                {
                    changed |= needs[flow].Add(permission);
                }
            }
        }
        return needs;
    }

    /// <summary>The flows a flow's steps can start. A templated target could be any of its kind.</summary>
    private static IEnumerable<string> Starts(AppConfig config, FlowDef flow)
    {
        foreach (var step in flow.Steps)
        {
            var (kind, target) = step.Action switch
            {
                "flow.invoke@1" => ("manual", Literal(step.Input["flow"])),
                "event.publish@1" => ("event", Literal(step.Input["name"])),
                "records.create@1" or "records.update@1" or "records.delete@1" => ("row", Literal(step.Input["table"])),
                _ => (null, null),
            };
            foreach (var other in config.Flows.Where(f => kind switch
                     {
                         "manual" => f.Trigger.Event == "manual" && (target is null || f.ApiName == target),
                         "event" => f.Trigger.Event.StartsWith(AppEvent.FlowEventPrefix, StringComparison.Ordinal)
                                    && (target is null || f.Trigger.Event == AppEvent.FlowEventPrefix + target),
                         "row" => f.Trigger.TableId is { } t
                                  && (target is null || config.Tables.FirstOrDefault(x => x.Id == t)?.ApiName == target),
                         _ => false,
                     }))
            {
                yield return other.ApiName;
            }
        }
    }

    private static string? Literal(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !text.Contains("{{", StringComparison.Ordinal) ? text : null;

    private const int MaxActionsPerProvider = 50;

    [GeneratedRegex("^[a-z][a-z0-9-]*\\.[a-z0-9.-]{1,62}$")]
    private static partial Regex ProvidedName();
}

/// <summary>An action another plugin offers through <c>automation.actions@1</c>, run through the contract runtime.</summary>
internal sealed class ProvidedAction(IAutomationActionProvider provider, AutomationActionDescriptor offered) : IFlowAction
{
    public string Id => offered.Name;

    /// <summary>What a member must hold to publish a flow using this action; see <see cref="AutomationActionDescriptor.Permission"/>.</summary>
    public string? Permission => offered.Permission;
    public int Major => offered.Major;
    public string Description => offered.Description;
    public OpRisk Risk => offered.Risk;
    public JsonObject InputSchema => JsonNode.Parse(offered.InputSchema.GetRawText()) as JsonObject ?? [];

    public async Task<JsonNode?> RunAsync(FlowActionContext context, JsonObject input, CancellationToken ct)
    {
        var instanceId = context.Plugin.Instance?.InstanceId
            ?? throw new InvalidOperationException("Flow steps run per instance; this context has none.");
        // The permission as the provider asks for it now, against what the revision's publisher
        // was checked for: a revision published before the action needed it never inherits it.
        if (Permission is { } permission)
        {
            using var rls = RlsScope.Tenant(context.Plugin.TenantId);
            var db = context.Services.GetRequiredService<AppsDbContext>();
            var run = context.Run;
            var vouched = await db.Revisions.AsNoTracking()
                .Where(r => r.Number == run.Revision && db.Apps.Any(a => a.Id == r.AppId && a.InstanceId == run.InstanceId))
                .Select(r => r.VouchedPermissions).FirstOrDefaultAsync(ct);
            if (vouched is null || !vouched.Contains(permission))
            {
                throw new FlowFatalException(
                    $"{Id}@{Major} needs the {permission} permission, which whoever published revision {run.Revision} was not checked for. Publish the app again as someone who has it.");
            }
        }
        var result = await provider.ExecuteAsync(new AutomationActionCall(
            offered.Name, offered.Major, JsonSerializer.SerializeToElement(input), context.IdempotencyKey, instanceId, context.Run.Id), ct);
        return result.Output is { } output ? JsonNode.Parse(output.GetRawText()) : null;
    }
}

public static class AutomationLimits
{
    /// <summary>How many flow runs deep one original action may cascade.</summary>
    public const int MaxDepth = 5;

    public const int MaxSteps = 50;
    public const int MaxWrites = 100;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(60);

    /// <summary>How many runs one original action may set off in total, however they chain.</summary>
    public const int MaxRunsPerCorrelation = 20;

    /// <summary>Attempts at a run before it is failed for good.</summary>
    public const int MaxAttempts = 3;

    public const int MinScheduleMinutes = 5;
    public const int MaxScheduleMinutes = 7 * 24 * 60;
}

/// <summary>Shared plumbing for the built-in actions: strict input parsing and JSON output.</summary>
internal abstract partial class FlowAction<TInput> : IFlowAction
{
    public abstract string Id { get; }
    public virtual int Major => 1;
    public abstract string Description { get; }
    public abstract OpRisk Risk { get; }
    public JsonObject InputSchema => JsonNode.Parse(Schema)!.AsObject();
    protected abstract string Schema { get; }

    // Declared here, on the class that implements IFlowAction, so an override is what the
    // interface sees; a member declared only in a derived class would not replace the default.
    public virtual IReadOnlySet<string> HtmlInputs { get; } = new HashSet<string>();

    public async Task<JsonNode?> RunAsync(FlowActionContext context, JsonObject input, CancellationToken ct)
    {
        TInput parsed;
        try
        {
            parsed = input.Deserialize<TInput>(ConfigJson.Options)
                     ?? throw new FlowFatalException($"{Id}: the input is empty.");
        }
        catch (JsonException e)
        {
            throw new FlowFatalException($"{Id}: the input is not valid — {e.Message}");
        }
        return await RunAsync(context, parsed, ct);
    }

    protected abstract Task<JsonNode?> RunAsync(FlowActionContext context, TInput input, CancellationToken ct);

    protected static JsonNode? Json<T>(T value) => JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web);

    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,62}$")]
    protected static partial Regex EventName();
}

// ---------------------------------------------------------------------- records

internal sealed record RecordWrite(string Table, JsonObject Values, Guid? Id = null);
internal sealed record RecordRef(string Table, Guid Id);
internal sealed record RecordFind(string Table, Guid? Id = null, RecordFilter? Filter = null, IReadOnlyList<RecordSort>? Sort = null, int PageSize = 20);

internal sealed class RecordsCreateAction : FlowAction<RecordWrite>
{
    public override string Id => "records.create";
    public override string Description => "Create a record in one of this app's tables. Output: the record.";
    public override OpRisk Risk => OpRisk.Safe;
    protected override string Schema => """{"type":"object","required":["table","values"],"properties":{"table":{"type":"string"},"values":{"type":"object"}}}""";

    protected override async Task<JsonNode?> RunAsync(FlowActionContext context, RecordWrite input, CancellationToken ct)
    {
        context.CountWrite();
        return await context.Services.GetRequiredService<RecordService>().CreateAsync(input.Table, input.Values, RecordPlane.System, ct);
    }
}

internal sealed class RecordsUpdateAction : FlowAction<RecordWrite>
{
    public override string Id => "records.update";
    public override string Description => "Change fields of a record. Output: the record.";
    public override OpRisk Risk => OpRisk.Safe;
    protected override string Schema => """{"type":"object","required":["table","id","values"],"properties":{"table":{"type":"string"},"id":{"type":"string","format":"uuid"},"values":{"type":"object"}}}""";

    protected override async Task<JsonNode?> RunAsync(FlowActionContext context, RecordWrite input, CancellationToken ct)
    {
        context.CountWrite();
        var id = input.Id ?? throw new FlowFatalException("records.update needs an id.");
        return await context.Services.GetRequiredService<RecordService>().UpdateAsync(input.Table, id, input.Values, RecordPlane.System, ct)
               ?? throw new FlowFatalException($"There is no {input.Table} record {id}.");
    }
}

internal sealed class RecordsDeleteAction : FlowAction<RecordRef>
{
    public override string Id => "records.delete";
    public override string Description => "Delete a record. Output: { deleted }.";
    public override OpRisk Risk => OpRisk.Dangerous;
    protected override string Schema => """{"type":"object","required":["table","id"],"properties":{"table":{"type":"string"},"id":{"type":"string","format":"uuid"}}}""";

    protected override async Task<JsonNode?> RunAsync(FlowActionContext context, RecordRef input, CancellationToken ct)
    {
        context.CountWrite();
        var deleted = await context.Services.GetRequiredService<RecordService>().DeleteAsync(input.Table, input.Id, null, RecordPlane.System, ct);
        return new JsonObject { ["deleted"] = deleted };
    }
}

internal sealed class RecordsLookupAction : FlowAction<RecordFind>
{
    public override string Id => "records.lookup";
    public override string Description => "Find one record, by id or as the first match of a filter. Output: the record, or null.";
    public override OpRisk Risk => OpRisk.Read;
    protected override string Schema => """{"type":"object","required":["table"],"properties":{"table":{"type":"string"},"id":{"type":"string","format":"uuid"},"filter":{"type":"object"},"sort":{"type":"array"}}}""";

    protected override async Task<JsonNode?> RunAsync(FlowActionContext context, RecordFind input, CancellationToken ct)
    {
        var records = context.Services.GetRequiredService<RecordService>();
        if (input.Id is { } id)
        {
            return await records.GetAsync(input.Table, id, [], RecordPlane.System, ct);
        }
        var page = await records.QueryAsync(input.Table, new RecordQuery { Filter = input.Filter, Sort = input.Sort ?? [], PageSize = 1 }, RecordPlane.System, ct);
        return page.Items.FirstOrDefault();
    }
}

internal sealed class RecordsQueryAction : FlowAction<RecordFind>
{
    public override string Id => "records.query";
    public override string Description => "Records matching a filter, up to 50. Output: { items, total }.";
    public override OpRisk Risk => OpRisk.Read;
    protected override string Schema => """{"type":"object","required":["table"],"properties":{"table":{"type":"string"},"filter":{"type":"object"},"sort":{"type":"array"},"pageSize":{"type":"integer","minimum":1,"maximum":50}}}""";

    protected override async Task<JsonNode?> RunAsync(FlowActionContext context, RecordFind input, CancellationToken ct)
    {
        var page = await context.Services.GetRequiredService<RecordService>().QueryAsync(input.Table,
            new RecordQuery { Filter = input.Filter, Sort = input.Sort ?? [], PageSize = Math.Clamp(input.PageSize, 1, 50) }, RecordPlane.System, ct);
        return new JsonObject { ["items"] = new JsonArray(page.Items.Select(i => (JsonNode)i).ToArray()), ["total"] = page.Total };
    }
}

// ---------------------------------------------------------------------- flows and events

internal sealed record InvokeInput(string Flow, JsonObject? Input = null);
internal sealed record PublishInput(string Name, JsonObject? Payload = null);

internal sealed class FlowInvokeAction : FlowAction<InvokeInput>
{
    public override string Id => "flow.invoke";
    public override string Description => "Start another flow of this app (one with a manual trigger), passing it input. Output: { runId }.";
    public override OpRisk Risk => OpRisk.Safe;
    protected override string Schema => """{"type":"object","required":["flow"],"properties":{"flow":{"type":"string"},"input":{"type":"object"}}}""";

    protected override async Task<JsonNode?> RunAsync(FlowActionContext context, InvokeInput input, CancellationToken ct)
    {
        var runs = context.Services.GetRequiredService<FlowRunQueue>();
        var origin = new EventOrigin(context.Run.CorrelationId, context.Run.Id, context.Run.Depth + 1);
        var runId = await runs.StartManualAsync(input.Flow, input.Input ?? [], origin, $"{context.IdempotencyKey}", context.Run.Revision, ct);
        return new JsonObject { ["runId"] = runId.ToString() };
    }
}

internal sealed class EventPublishAction : FlowAction<PublishInput>
{
    public override string Id => "event.publish";
    public override string Description => "Announce flow.event.{name} with a payload; flows triggered by that event run. Output: { eventName }.";
    public override OpRisk Risk => OpRisk.Safe;
    protected override string Schema => """{"type":"object","required":["name"],"properties":{"name":{"type":"string","pattern":"^[a-z][a-z0-9_.-]{0,62}$"},"payload":{"type":"object"}}}""";

    protected override async Task<JsonNode?> RunAsync(FlowActionContext context, PublishInput input, CancellationToken ct)
    {
        if (!EventName().IsMatch(input.Name))
        {
            throw new FlowFatalException($"'{input.Name}' is not a valid event name.");
        }
        var db = context.Services.GetRequiredService<AppsDbContext>();
        var name = AppEvent.FlowEventPrefix + input.Name;
        context.Services.GetRequiredService<AppEventLog>().Add(db, context.Run.Revision, name,
            new AppEventEntity("flow", context.Run.FlowId), input.Payload ?? []);
        await db.SaveChangesAsync(ct);
        return new JsonObject { ["eventName"] = name };
    }
}

// ---------------------------------------------------------------------- platform

internal sealed record EmailInput(JsonNode To, string Subject, string? Text = null, string? Html = null, string? ReplyTo = null);
internal sealed record NotifyInput(string Title, string Body, string? Severity = null, string? LinkPath = null);

internal sealed class EmailSendAction : FlowAction<EmailInput>
{
    public override string Id => "dcms.email.send";

    // The body is HTML the tenant wrote; what a template puts into it is data, never markup.
    public override IReadOnlySet<string> HtmlInputs { get; } = new HashSet<string> { "html" };
    public override string Description => "Send an email through the platform's queue (at most 20 recipients). Text is escaped; in html, the template's own markup is kept and every {{ }} value is escaped. Output: { recipients }.";
    public override OpRisk Risk => OpRisk.Safe;
    protected override string Schema => """{"type":"object","required":["to","subject"],"properties":{"to":{"oneOf":[{"type":"string"},{"type":"array","items":{"type":"string"},"maxItems":20}]},"subject":{"type":"string"},"text":{"type":"string"},"html":{"type":"string"},"replyTo":{"type":"string"}}}""";

    protected override async Task<JsonNode?> RunAsync(FlowActionContext context, EmailInput input, CancellationToken ct)
    {
        var to = input.To switch
        {
            JsonArray list => list.Select(a => a?.GetValue<string>() ?? "").ToList(),
            JsonValue one => [one.GetValue<string>()],
            _ => [],
        };
        to = to.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (to.Count is 0 or > 20)
        {
            throw new FlowFatalException("dcms.email.send needs 1 to 20 recipients.");
        }
        // Template values are already encoded; the whole body is then sanitized too, because
        // encoding alone does not make every context safe (an unquoted attribute, a
        // javascript: link) and the tenant's own markup is mail sent in its name all the same.
        var html = input.Html is { } markup
            ? HtmlContentSanitizer.Sanitize(markup) ?? ""
            : WebUtility.HtmlEncode(input.Text ?? "").Replace("\n", "<br>", StringComparison.Ordinal);
        var queued = await context.Plugin.Contracts.Get<IPluginEmail>().SendAsync(
            new EmailSend(to, input.Subject, html, input.ReplyTo, context.IdempotencyKey), ct);
        return new JsonObject { ["recipients"] = queued.Recipients };
    }
}

internal sealed class NotificationRaiseAction : FlowAction<NotifyInput>
{
    public override string Id => "dcms.notifications.raise";
    public override string Description => "Raise an in-app notification for members who may read this app's records. Output: {}.";
    public override OpRisk Risk => OpRisk.Safe;
    protected override string Schema => """{"type":"object","required":["title","body"],"properties":{"title":{"type":"string"},"body":{"type":"string"},"severity":{"enum":["info","warning","error"]},"linkPath":{"type":"string"}}}""";

    protected override async Task<JsonNode?> RunAsync(FlowActionContext context, NotifyInput input, CancellationToken ct)
    {
        var severity = input.Severity switch
        {
            "warning" => NotificationSeverity.Warning,
            "error" => NotificationSeverity.Error,
            _ => NotificationSeverity.Info,
        };
        await context.Plugin.Contracts.Get<IPluginNotifications>().RaiseAsync(new NotificationRaise(
            input.Title, input.Body, DynamicAppsPermissions.DataRead, context.IdempotencyKey, severity,
            input.LinkPath ?? $"/plugins/{context.Plugin.Instance?.Slug}"), ct);
        return new JsonObject();
    }
}

// ---------------------------------------------------------------------- other plugins, through contracts

internal sealed record ContentInput(string ContentType, string? Slug = null, int Page = 1, int PageSize = 20, Guid? InstanceId = null);
internal sealed record VisitorInput(Guid VisitorId);

internal sealed class ContentGetAction : FlowAction<ContentInput>
{
    public override string Id => "content.get";
    public override string Description => "One published content item (any content plugin of this site) by type and slug. Output: the item, or null.";
    public override OpRisk Risk => OpRisk.Read;
    protected override string Schema => """{"type":"object","required":["contentType","slug"],"properties":{"contentType":{"type":"string"},"slug":{"type":"string"},"instanceId":{"type":"string","format":"uuid"}}}""";

    protected override async Task<JsonNode?> RunAsync(FlowActionContext context, ContentInput input, CancellationToken ct) =>
        Json(await context.Plugin.Contracts.Get<IPluginContent>().GetBySlugAsync(
            new ContentLookup(input.ContentType, input.Slug ?? throw new FlowFatalException("content.get needs a slug."), input.InstanceId), ct));
}

internal sealed class ContentListAction : FlowAction<ContentInput>
{
    public override string Id => "content.list";
    public override string Description => "A page of published content items of one type (at most 50). Output: { items, page, pageSize, totalCount }.";
    public override OpRisk Risk => OpRisk.Read;
    protected override string Schema => """{"type":"object","required":["contentType"],"properties":{"contentType":{"type":"string"},"page":{"type":"integer"},"pageSize":{"type":"integer","maximum":50},"instanceId":{"type":"string","format":"uuid"}}}""";

    protected override async Task<JsonNode?> RunAsync(FlowActionContext context, ContentInput input, CancellationToken ct) =>
        Json(await context.Plugin.Contracts.Get<IPluginContent>().ListAsync(
            new ContentListRequest(input.ContentType, Math.Max(1, input.Page), Math.Clamp(input.PageSize, 1, 50), input.InstanceId), ct));
}

internal sealed class VisitorLookupAction : FlowAction<VisitorInput>
{
    public override string Id => "visitor.lookup";
    public override string Description => "A site visitor's profile (email, display name, attributes shared with plugins), through VisitorAuth. Output: the profile, or null.";
    public override OpRisk Risk => OpRisk.Read;
    protected override string Schema => """{"type":"object","required":["visitorId"],"properties":{"visitorId":{"type":"string","format":"uuid"}}}""";

    protected override async Task<JsonNode?> RunAsync(FlowActionContext context, VisitorInput input, CancellationToken ct)
    {
        var profiles = context.Plugin.Contracts.TryGet<IVisitorProfiles>()
                       ?? throw new FlowFatalException("visitor.lookup needs the Visitor accounts plugin on this site.");
        return Json(await profiles.GetAsync(new VisitorRef(input.VisitorId), ct));
    }
}
