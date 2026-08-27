using System.Diagnostics;
using Dcms.Shared.Audit;
using Dcms.Shared.Contracts.Messaging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.AdminApi.Audit;

/// <summary>
/// Projects every chained audit record onto the log pipeline, so the audit log has a
/// searchable presence in Loki next to the traces it already shares a <c>TraceId</c> with.
///
/// <para><b>Why this exists.</b> <c>AuditChainWriter</c> publishes each record to
/// <c>audit.recorded</c> after the chain append commits, and until now nothing read that
/// subject — the provisioning script calls it the designed extension point for exactly this.
/// Postgres remains the system of record and the only thing anyone verifies a chain against;
/// this is a read-only projection that makes "what happened around 14:05" answerable in the
/// same place as the logs and traces, rather than requiring a SQL session.</para>
///
/// <para><b>It is deliberately lossy in one direction: identity.</b> The record's actor
/// display name is usually an email address, and it is dropped here. The actor id and ref go
/// out instead, which is enough to resolve who acted from the audit table, and which keeps
/// personal data out of a 90-day log store that is queried far more casually than the audit
/// schema is. The same reasoning governs the <c>obs.*</c> reporting views. <c>Changes</c> are
/// dropped too: they are already redacted, but their field values are the most sensitive thing
/// the platform stores, and a log line is not the place to re-derive that judgement.</para>
///
/// <para>Failure here costs a log line and nothing else. The record is already chained and
/// already durable before this consumer ever sees it, so a projection error acks and moves on
/// rather than redelivering forever.</para>
/// </summary>
public sealed class AuditLogProjector(
    INatsJSContext jetStream,
    ILogger<AuditLogProjector> logger) : BackgroundService
{
    private const string DurableName = "audit-log-projector";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                    Streams.Audit,
                    new ConsumerConfig(DurableName)
                    {
                        FilterSubject = Subjects.AuditRecorded,
                        AckPolicy = ConsumerConfigAckPolicy.Explicit,
                        // The chain writer is the pace-setter; this consumer only formats. A
                        // deep ack-pending window here would let a slow log sink build a
                        // backlog on a stream whose retention is now capped.
                        MaxAckPending = 64,
                    },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<AuditEvent>(cancellationToken: stoppingToken))
                {
                    Project(msg.Data);
                    await msg.AckAsync(cancellationToken: stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Audit log projection unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private void Project(AuditEvent? @event)
    {
        if (@event is null)
        {
            return;
        }

        try
        {
            // The record's own trace, not this consumer's. Restoring it means a line found in
            // Loki links to the trace of the request that caused the action, minutes earlier
            // and in another service — which is the entire point of projecting these at all.
            using var activity = StartLinked(@event);

            // Severity is carried across rather than flattened to Information: the runbook's
            // "AUDIT ANCHOR" guidance depends on Critical records being findable as Critical,
            // and a projection that levels everything would quietly break that.
            var level = @event.Severity switch
            {
                AuditSeverity.Critical => LogLevel.Critical,
                AuditSeverity.Warning => LogLevel.Warning,
                AuditSeverity.Notice => LogLevel.Information,
                _ => LogLevel.Information,
            };

#pragma warning disable CA2254 // The template is constant; only the level varies.
            logger.Log(
                level,
                "AUDIT {AuditAction} {AuditOutcome} tenant={AuditTenantId} actor={AuditActorKind}/{AuditActorId} " +
                "resource={AuditResourceType}/{AuditResourceId} service={AuditServiceName} seq={AuditProducerSeq} " +
                "correlation={AuditCorrelationId} trace={AuditTraceId} sandbox={AuditSandbox} event={AuditEventId}",
                @event.Action,
                @event.Outcome,
                @event.TenantId == Guid.Empty ? "platform" : @event.TenantId.ToString(),
                @event.Actor.Kind,
                // Id when it is a user, Ref when the identity is a client_id, service or repo.
                // Never Display — see the class remarks.
                @event.Actor.Id?.ToString() ?? @event.Actor.Ref ?? "-",
                @event.ResourceType ?? "-",
                @event.ResourceId ?? "-",
                @event.ServiceName,
                @event.ProducerSeq,
                @event.CorrelationId ?? "-",
                @event.TraceId ?? "-",
                @event.IsSandbox,
                @event.EventId);
#pragma warning restore CA2254
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not project audit record {EventId}.", @event.EventId);
        }
    }

    /// <summary>
    /// A span linked to the record's original trace, so the projected line carries that trace
    /// id rather than this background loop's. Returns null when the record has no trace — a
    /// worker-originated action, for instance — in which case the line still stands on its own
    /// correlation id.
    /// </summary>
    private static Activity? StartLinked(AuditEvent @event)
    {
        if (@event.TraceId is null || @event.SpanId is null)
        {
            return null;
        }
        // Reassembled into a traceparent and handed to ActivityContext.TryParse rather than
        // parsed field by field: the id types have no TryParse, only a CreateFromString that
        // throws, and a stored id that is somehow malformed must not become an exception on a
        // path whose only job is to write a log line.
        if (!ActivityContext.TryParse($"00-{@event.TraceId}-{@event.SpanId}-01", null, out var parent))
        {
            return null;
        }
        return Shared.Telemetry.DcmsActivitySource.StartLinked("audit.project", parent, ActivityKind.Consumer);
    }
}
