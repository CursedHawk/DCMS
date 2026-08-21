using Dcms.Shared.Audit;
using Dcms.Shared.Contracts.Messaging;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;

namespace Dcms.Shared.Messaging;

/// <summary>
/// The audit write path for the two services that cannot reach the <c>audit</c> schema:
/// email-worker, which has no database connection at all, and site-builder, which connects as
/// <c>dcms_sitebuilder</c> with <c>USAGE</c> on <c>sites</c> and nothing else.
///
/// <para>Everywhere else the record is a row in the transaction that carries the change, which
/// is stronger than anything a message bus can offer. These two cannot have that, so they get
/// the next thing: an acknowledged JetStream publish, drained into the chain by admin-api's
/// writer. At-least-once delivery is fine — the append deduplicates on
/// <c>(OccurredAt, EventId)</c> inside the transaction that bumps the chain head.</para>
///
/// <para><b>No buffering channel, deliberately.</b> One would exist to keep a slow publish off
/// a request path, and neither of these services has one: they drain a queue, so waiting for
/// the ack is back-pressure on a background loop rather than latency for a person. If the
/// publish fails the record is written to the log at <c>Critical</c>, in full — a line outside
/// the database is the floor this system is built on, and it is worth more than a record held
/// in memory by a process that may be about to stop.</para>
/// </summary>
public sealed class NatsAuditSink(
    INatsJSContext jetStream,
    AuditEventJson json,
    ILogger<NatsAuditSink> logger) : IAuditSink
{
    public async ValueTask WriteAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken = default)
    {
        foreach (var @event in events)
        {
            try
            {
                var ack = await jetStream.PublishAsync(
                    Subjects.AuditSubmitted,
                    @event,
                    // The event id doubles as the idempotency key, so a retry after an
                    // ambiguous failure does not enqueue a second copy of the same record.
                    opts: new NatsJSPubOpts { MsgId = @event.EventId.ToString() },
                    cancellationToken: cancellationToken);
                ack.EnsureSuccess();
            }
            catch (Exception ex)
            {
                logger.LogCritical(
                    ex,
                    "Audit record could not be published and survives only in this line. Action: {Action}. "
                    + "Tenant: {TenantId}. CorrelationId: {CorrelationId}. Payload: {Payload}",
                    @event.Action,
                    @event.TenantId,
                    @event.CorrelationId,
                    json.Serialize(@event));
            }
        }
    }
}

/// <summary>
/// Serialises a record for the log line of last resort. A separate type so the sink does not
/// have to depend on the EF assembly's serializer, and so the two cannot drift into writing
/// the payload one way and reading it another.
/// </summary>
public sealed class AuditEventJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    public string Serialize(AuditEvent @event) => System.Text.Json.JsonSerializer.Serialize(@event, Options);
}
