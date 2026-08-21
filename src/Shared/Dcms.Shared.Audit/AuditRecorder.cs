using Dcms.Shared.Kernel.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Audit;

/// <summary>
/// Buffers entries and completes them with the ambient envelope at flush.
///
/// The envelope is resolved <b>at flush time, not at record time</b>, because most of it is
/// not knowable when a handler records its action: the status code is settled after the
/// response is produced, and the actor is cheaper to resolve once for a whole request than
/// per entry.
/// </summary>
public sealed class AuditRecorder(
    AuditScope scope,
    AuditServiceIdentity identity,
    IAuditSink sink,
    ICurrentActor actor,
    ITenantContext tenantContext,
    IClock clock,
    AuditMetrics metrics,
    ILogger<AuditRecorder> logger) : IAuditRecorder
{
    private readonly List<AuditEntry> _pending = [];

    public IReadOnlyList<AuditEntry> Pending => _pending;

    public AuditEntry Record(string action)
    {
        var entry = new AuditEntry { Action = action };
        Record(entry);
        return entry;
    }

    public void Record(AuditEntry entry)
    {
        _pending.Add(entry);
        scope.HasExplicitRecord = true;
    }

    public AuditEntry? Declared => scope.Declared;

    public AuditEntry Declare(string action)
    {
        if (scope.Declared is not { } declared)
        {
            return Record(action);
        }

        declared.Action = action;
        return declared;
    }

    public void Discard(AuditEntry entry)
    {
        _pending.Remove(entry);
        if (ReferenceEquals(scope.Declared, entry))
        {
            scope.Declared = null;
        }
    }

    public async ValueTask RecordNowAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        scope.HasExplicitRecord = true;
        // Deliberately not wrapped in try/catch: the callers that reach for this one are the
        // ones that must not proceed unrecorded.
        await sink.WriteAsync([Complete(entry)], cancellationToken);
    }

    public IReadOnlyList<AuditEvent> Drain()
    {
        if (_pending.Count == 0)
        {
            return [];
        }

        var events = new AuditEvent[_pending.Count];
        for (var i = 0; i < _pending.Count; i++)
        {
            events[i] = Complete(_pending[i]);
        }
        _pending.Clear();
        return events;
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        var events = Drain();
        if (events.Count == 0)
        {
            return;
        }

        try
        {
            await sink.WriteAsync(events, cancellationToken);
            metrics.RecordWritten(events.Count);
        }
        catch (Exception ex)
        {
            metrics.RecordSinkFailure(events.Count);
            // Last resort, and the reason this is Critical rather than Error: the record is
            // now only in the log, so the log line has to carry enough to reconstruct it.
            logger.LogCritical(
                ex,
                "Audit sink failed; {Count} record(s) survive only here. Actions: {Actions}. CorrelationId: {CorrelationId}.",
                events.Count,
                string.Join(", ", events.Select(e => e.Action)),
                scope.CorrelationId);
        }
    }

    private AuditEvent Complete(AuditEntry entry)
    {
        // The resolver is normally installed by whatever opened the scope; this covers the
        // paths that record without one — a background service resolving the recorder directly.
        scope.ActorResolver ??= () => new AuditActor(
            actor.Kind, actor.Id, actor.Key, actor.Display, AuditAttribution.Direct);

        var resolved = scope.ResolveActor();

        return new AuditEvent
        {
            EventId = entry.EventId,
            OccurredAt = entry.OccurredAt ?? clock.UtcNow,

            // Explicit entry tenant wins; then the scope (set by consumers from the message);
            // then the ambient HTTP tenant. Guid.Empty is the platform-scope sentinel, and is
            // also what a tenant-less request correctly lands on.
            TenantId = entry.TenantId ?? scope.TenantId ?? tenantContext.TenantId ?? Guid.Empty,

            Action = entry.Action,
            Category = entry.Category,
            Outcome = entry.Outcome,
            Severity = entry.Severity,

            Actor = resolved,
            SubjectUserId = entry.SubjectUserId,

            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            ResourceLabel = entry.ResourceLabel,

            ServiceName = identity.ServiceName,
            ServiceInstance = identity.Instance,
            ProducerSeq = identity.NextSequence(),

            CorrelationId = scope.CorrelationId,
            CausationId = entry.CausationId ?? scope.CausationId,
            TraceId = scope.TraceId,
            SpanId = scope.SpanId,

            Http = scope.Http,
            IsSandbox = scope.IsSandbox,

            Metadata = BuildMetadata(entry),
            Changes = entry.Changes,
            RedactionVersion = entry.RedactionVersion,
        };
    }

    private Dictionary<string, object?> BuildMetadata(AuditEntry entry)
    {
        var metadata = entry.Metadata is null
            ? []
            : new Dictionary<string, object?>(entry.Metadata);

        // The gating permission is ambient rather than per-entry, but it belongs on the
        // record: "who was allowed to do this, and under which key" is half of an
        // authorization investigation.
        if (scope.Permission is not null && !metadata.ContainsKey("permission"))
        {
            metadata["permission"] = scope.Permission;
        }

        return metadata;
    }
}
