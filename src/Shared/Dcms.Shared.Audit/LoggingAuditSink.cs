using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Audit;

/// <summary>
/// The floor. Serialises each record to a <c>Critical</c> log line, which leaves the process
/// for stdout and whatever collects it — deliberately *outside* the database, so it survives
/// the failure modes that a database-backed sink cannot.
///
/// Registered as the default so that a service which has not yet been given a real sink
/// still records rather than silently discarding; also used as the last-resort fallback when
/// a real sink is unavailable. Critical rather than Information because a record reaching
/// only this sink means the intended path failed, and that should page someone.
/// </summary>
public sealed class LoggingAuditSink(ILogger<LoggingAuditSink> logger) : IAuditSink
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public ValueTask WriteAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken = default)
    {
        foreach (var @event in events)
        {
            logger.LogCritical(
                "AUDIT {Action} tenant={TenantId} actor={ActorKind}:{ActorRef} outcome={Outcome} payload={Payload}",
                @event.Action,
                @event.TenantId,
                @event.Actor.Kind,
                @event.Actor.Ref ?? @event.Actor.Id?.ToString() ?? "-",
                @event.Outcome,
                JsonSerializer.Serialize(@event, Json));
        }
        return ValueTask.CompletedTask;
    }
}
