using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.Plugins.Forms.Api;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>
/// Turns other plugins' typed events into runtime events, so a flow can trigger on
/// <c>visitor.registered</c> or <c>form.submitted</c> like on a row change (ADR 0021). Each
/// becomes an outbox row for every app of the tenant whose live configuration has a flow on
/// it; the worker routes it from there, as it does a row event. The event id is derived from
/// the platform event, so a redelivery adds nothing.
/// </summary>
internal sealed class PlatformEventBridge(AppsDbContext db, AppEventLog events) :
    IPluginEventHandler<VisitorRegistered>,
    IPluginEventHandler<FormSubmitted>
{
    public const string VisitorRegisteredEvent = "visitor.registered";
    public const string FormSubmittedEvent = "form.submitted";

    /// <summary>The platform events a flow may trigger on.</summary>
    public static readonly IReadOnlySet<string> Events = new HashSet<string>(StringComparer.Ordinal) { VisitorRegisteredEvent, FormSubmittedEvent };

    public Task HandleAsync(VisitorRegistered e, IPluginContext context, CancellationToken ct) =>
        ForwardAsync(context, VisitorRegisteredEvent, e.VisitorId, new AppEventEntity("visitor", e.VisitorId),
            () => Task.FromResult(new JsonObject { ["visitorId"] = e.VisitorId.ToString() }), ct);

    public Task HandleAsync(FormSubmitted e, IPluginContext context, CancellationToken ct) =>
        ForwardAsync(context, FormSubmittedEvent, e.SubmissionId, new AppEventEntity("form_submission", e.SubmissionId), async () =>
        {
            var payload = new JsonObject
            {
                ["submissionId"] = e.SubmissionId.ToString(),
                ["formInstanceId"] = e.InstanceId.ToString(),
                ["formName"] = e.FormName,
                ["visitorId"] = e.VisitorId?.ToString(),
            };
            // What the visitor sent, so a flow can file it without a lookup step. The Forms plugin
            // bounds a submission's size; a Forms instance switched off since leaves it out.
            if (context.Contracts.TryGet<IFormSubmissions>(e.InstanceId) is { } forms
                && await forms.GetAsync(new SubmissionRef(e.SubmissionId), ct) is { } submission)
            {
                payload["data"] = JsonSerializer.SerializeToNode(submission.Data, JsonSerializerOptions.Web);
            }
            return payload;
        }, ct);

    private async Task ForwardAsync(IPluginContext context, string name, Guid key, AppEventEntity entity, Func<Task<JsonObject>> payload,
        CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var apps = await LiveAppsOnAsync(name, ct);
        if (apps.Count == 0)
        {
            return;
        }
        var body = await payload();
        foreach (var app in apps)
        {
            var eventId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{name}:{key:N}:{app.InstanceId:N}")).AsSpan(0, 16));
            if (!await db.Outbox.AnyAsync(m => m.Id == eventId, ct))
            {
                events.Add(db, app.InstanceId, eventId, app.Revision, name, entity, (JsonObject)body.DeepClone());
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Apps whose published configuration has a flow triggered by the event (enabled or not; the router decides).</summary>
    private Task<List<LiveApp>> LiveAppsOnAsync(string name, CancellationToken ct)
    {
        var wanted = new JsonObject { ["flows"] = new JsonArray(new JsonObject { ["trigger"] = new JsonObject { ["event"] = name } }) }.ToJsonString();
        return db.Apps
            .Join(db.Revisions, a => a.PublishedRevisionId, r => r.Id, (a, r) => new { a.InstanceId, r.Number, r.Snapshot })
            .Where(x => EF.Functions.JsonContains(x.Snapshot, wanted))
            .Select(x => new LiveApp(x.InstanceId, x.Number))
            .ToListAsync(ct);
    }

    private sealed record LiveApp(Guid InstanceId, int Revision);
}
