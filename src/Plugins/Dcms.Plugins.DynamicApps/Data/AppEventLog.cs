using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Data.DynamicApps;

namespace Dcms.Plugins.DynamicApps.Data;

/// <summary>Where the writes of one scope come from: a person or the site (depth 0), or a flow run.</summary>
public sealed record EventOrigin(Guid CorrelationId, Guid? CausationId, int Depth)
{
    public static EventOrigin New() => new(Guid.NewGuid(), null, 0);
}

/// <summary>
/// Adds runtime events to the outbox through the caller's own <see cref="AppsDbContext"/>, so
/// an event is saved by the same <c>SaveChanges</c>, in the same transaction, as the change it
/// describes — never "save, then publish". Scoped: one correlation per request, job or flow run.
/// </summary>
public sealed class AppEventLog(IPluginContext context, AuditScope scope, TimeProvider clock)
{
    /// <summary>Set by a flow run before it writes, so what it causes carries its depth and causation.</summary>
    public EventOrigin Origin { get; set; } = EventOrigin.New();

    public void Add(AppsDbContext db, int revision, string eventName, AppEventEntity entity, JsonObject payload,
        IReadOnlyList<string>? changedFields = null) =>
        Add(db, context.Instance?.InstanceId ?? throw new InvalidOperationException("Dynamic Apps events are per instance; this context has none."),
            Guid.NewGuid(), revision, eventName, entity, payload, changedFields);

    /// <summary>For one named instance, with the caller's event id (deterministic, when a redelivery must not add a second event).</summary>
    public void Add(AppsDbContext db, Guid instanceId, Guid eventId, int revision, string eventName, AppEventEntity entity, JsonObject payload,
        IReadOnlyList<string>? changedFields = null)
    {
        var evt = new AppEvent
        {
            EventId = eventId,
            TenantId = context.TenantId,
            SourceInstanceId = instanceId,
            EventName = eventName,
            OccurredAt = clock.GetUtcNow(),
            Revision = revision,
            Entity = entity,
            Payload = payload,
            ChangedFields = changedFields ?? [],
            CorrelationId = Origin.CorrelationId,
            CausationId = Origin.CausationId,
            Depth = Origin.Depth,
        };
        db.Outbox.Add(new AppOutboxMessage
        {
            Id = evt.EventId,
            TenantId = evt.TenantId,
            InstanceId = instanceId,
            EventName = eventName,
            Envelope = JsonSerializer.Serialize(evt, JsonSerializerOptions.Web),
            OccurredAt = evt.OccurredAt,
            // What a flow does in reaction still names the person whose change set it off.
            ContextJson = AuditPropagation.CaptureJson(scope),
        });
    }
}
