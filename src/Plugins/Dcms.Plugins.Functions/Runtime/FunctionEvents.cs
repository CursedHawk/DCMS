using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.Forms.Api;
using Dcms.Plugins.UserAuth.Api;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Functions.Runtime;

/// <summary>
/// <c>dynamic-apps.event-sink@1</c> for one Functions instance: a committed row or relation event
/// of an app in this environment starts every function of the live deployment triggered by it.
/// The run carries the event's correlation, one level deeper, so a cascade is counted and stopped.
/// </summary>
internal sealed class AppEventSink(IServiceProvider services, IPluginContext context) : IAppEventSink
{
    public async Task<int> DeliverAsync(AppEventDelivery input, CancellationToken ct)
    {
        var (app, evt) = (input.App, input.Event);
        var (kind, verb) = evt.EventName.Split('.') is [var k, var v] && k is "row" or "relation" ? (k, v) : ("", "");
        if (kind == "" || context.Instance is not { } instance)
        {
            return 0;
        }
        var target = kind == "row" ? $"{app}.{evt.Entity?.Type}" : $"{app}.{evt.Payload["relationship"]?.GetValue<string>()}";

        using var rls = RlsScope.Environment(context.TenantId, context.EnvironmentId);
        var db = services.GetRequiredService<FunctionsDbContext>();
        var live = await RunQueue.Live(db.Deployments.Where(d => d.InstanceId == instance.InstanceId && d.Status == FunctionDeploymentStatus.Active))
            .FirstOrDefaultAsync(ct);
        if (live is null)
        {
            return 0;
        }
        var queue = new RunQueue(db);
        var started = 0;
        foreach (var function in Build.FunctionManifest.Deserialize(live.Manifest)
                     .Where(f => f.Triggers.Any(t => Matches(t, kind, target, verb, evt.ChangedFields)
                                                     && Build.FunctionManifest.MayReceive(t, live.VouchedPermissions))))
        {
            var payload = new JsonObject
            {
                ["event"] = evt.EventName,
                ["table"] = kind == "row" ? target : null,
                ["relationship"] = kind == "relation" ? target : null,
                ["id"] = evt.Entity?.Id?.ToString(),
                ["changedFields"] = JsonSerializer.SerializeToNode(evt.ChangedFields),
            };
            foreach (var (key, value) in evt.Payload)
            {
                payload[key] ??= value?.DeepClone();
            }
            if (await queue.EnqueueAsync(live, function.Name, kind, evt.EventId, payload,
                    new JsonObject { ["event"] = evt.EventName, ["app"] = app, ["eventId"] = evt.EventId.ToString() },
                    new RunOrigin(evt.CorrelationId, evt.EventId, evt.Depth + 1), ct))
            {
                started++;
            }
        }
        return started;
    }

    private static bool Matches(JsonObject trigger, string kind, string target, string verb, IReadOnlyList<string> changed) =>
        trigger["kind"]?.GetValue<string>() == kind
        && trigger[kind == "row" ? "table" : "relationship"]?.GetValue<string>() == target
        && trigger["event"]?.GetValue<string>() == verb
        && (trigger["fields"] is not JsonArray { Count: > 0 } fields || fields.Any(f => changed.Contains(f?.GetValue<string>() ?? "")));
}

/// <summary>
/// Other plugins' events (<c>on.event(…)</c>): each starts every function on it in every live
/// deployment of the environment it happened in. The event id is derived from the platform event,
/// so a redelivery starts nothing twice.
/// </summary>
internal sealed class PlatformEventRuns(FunctionsDbContext db) :
    IPluginEventHandler<VisitorRegistered>,
    IPluginEventHandler<FormSubmitted>,
    IPluginEventHandler<UserInvited>,
    IPluginEventHandler<UserActivated>
{
    public Task HandleAsync(UserInvited e, IPluginContext context, CancellationToken ct) =>
        StartAsync(context, "user.invited", e.UserId, () => Task.FromResult(new JsonObject { ["userId"] = e.UserId.ToString(), ["email"] = e.Email }), ct);

    public Task HandleAsync(UserActivated e, IPluginContext context, CancellationToken ct) =>
        StartAsync(context, "user.activated", e.UserId, () => Task.FromResult(new JsonObject { ["userId"] = e.UserId.ToString(), ["email"] = e.Email }), ct);

    public Task HandleAsync(VisitorRegistered e, IPluginContext context, CancellationToken ct) =>
        StartAsync(context, "visitor.registered", e.VisitorId, () => Task.FromResult(new JsonObject { ["visitorId"] = e.VisitorId.ToString() }), ct);

    public Task HandleAsync(FormSubmitted e, IPluginContext context, CancellationToken ct) =>
        StartAsync(context, "form.submitted", e.SubmissionId, async () =>
        {
            var payload = new JsonObject
            {
                ["submissionId"] = e.SubmissionId.ToString(),
                ["formInstanceId"] = e.InstanceId.ToString(),
                ["formName"] = e.FormName,
                ["visitorId"] = e.VisitorId?.ToString(),
            };
            // What was sent, so a function can file it without asking; the Forms plugin bounds its size.
            if (context.Contracts.TryGet<IFormSubmissions>(e.InstanceId) is { } forms
                && await forms.GetAsync(new SubmissionRef(e.SubmissionId), ct) is { } submission)
            {
                payload["data"] = JsonSerializer.SerializeToNode(submission.Data, JsonSerializerOptions.Web);
            }
            return payload;
        }, ct);

    private async Task StartAsync(IPluginContext context, string name, Guid key, Func<Task<JsonObject>> payload, CancellationToken ct)
    {
        // The environment the event happened in, as its consumer entered it (EventEnvironment);
        // never "every environment", which an unscoped query of deployments would be.
        using var rls = RlsScope.Environment(context.TenantId, RlsScope.EnvironmentFor(context.TenantId) ?? context.TenantId);
        var queue = new RunQueue(db);
        var on = await queue.OnAsync(t => t["kind"]?.GetValue<string>() == "event" && t["name"]?.GetValue<string>() == name, ct);
        if (on.Count == 0)
        {
            return;
        }
        var input = await payload();
        foreach (var (live, functions) in on)
        {
            foreach (var function in functions)
            {
                await queue.EnqueueAsync(live, function.Name, "event", RunQueue.EventId($"{name}:{key:N}:{live.InstanceId:N}"),
                    input.DeepClone(), new JsonObject { ["event"] = name }, RunOrigin.New(), ct);
            }
        }
    }
}
