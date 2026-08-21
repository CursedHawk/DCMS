using Dcms.Shared.Audit;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Audit;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.AdminApi.Audit;

/// <summary>
/// Brings in the records that could not be written where they were made.
///
/// <para>email-worker has no database connection and site-builder connects as
/// <c>dcms_sitebuilder</c>, which has <c>USAGE</c> on <c>sites</c> and nothing else. Both
/// publish their records to <c>audit.submitted</c>; this drains that into
/// <c>audit.audit_outbox</c>, from where the ordinary chain writer picks them up.</para>
///
/// <para><b>Into the outbox, not straight into the chain.</b> There is one append path and it
/// has the dedup check and the head bump inside a single transaction. A second writer reaching
/// the chain from a different direction would have to reproduce that, and two implementations
/// of a serialisation invariant is one too many.</para>
///
/// <para>At-least-once delivery is fine and is the reason the outbox has a unique
/// <c>EventId</c>: a redelivered record collides there and is dropped, long before it could
/// occupy a second sequence number in a tenant's chain.</para>
/// </summary>
public sealed class AuditIngestConsumer(
    INatsJSContext jetStream,
    IServiceProvider services,
    AuditEventSerializer serializer,
    ILogger<AuditIngestConsumer> logger) : BackgroundService
{
    private const string DurableName = "audit-ingest";

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
                        FilterSubject = Subjects.AuditSubmitted,
                        AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<AuditEvent>(cancellationToken: stoppingToken))
                {
                    await HandleAsync(msg, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Audit ingest unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleAsync(INatsJSMsg<AuditEvent> msg, CancellationToken ct)
    {
        var @event = msg.Data;
        if (@event is null)
        {
            // Undecodable. Acking rather than redelivering forever is the lesser evil, but the
            // log line is Critical: a record arrived and could not be stored, which is exactly
            // the loss this system exists to make impossible.
            logger.LogCritical("Audit record on {Subject} could not be decoded; it is lost.", Subjects.AuditSubmitted);
            await msg.AckTerminateAsync(cancellationToken: ct);
            return;
        }

        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

            // The outbox's unique EventId makes redelivery harmless; check first so a duplicate
            // is not an exception on a path that would then retry forever.
            if (await db.Outbox.AnyAsync(o => o.EventId == @event.EventId, ct))
            {
                await msg.AckAsync(cancellationToken: ct);
                return;
            }

            db.Outbox.Add(new AuditOutboxMessage
            {
                EventId = @event.EventId,
                // Producer clock, carried through unchanged: it is half the dedup key and the
                // partition key, and re-stamping it here would put the record in the wrong month.
                OccurredAt = @event.OccurredAt,
                PayloadJson = serializer.Serialize(@event),
            });
            await db.SaveChangesAsync(ct);

            await msg.AckAsync(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Do not ack: leave it on the stream so a database blip does not destroy the record.
            logger.LogError(ex, "Failed ingesting audit record {EventId}; it will be redelivered.", @event.EventId);
            await msg.NakAsync(delay: TimeSpan.FromSeconds(5), cancellationToken: ct);
        }
    }
}
