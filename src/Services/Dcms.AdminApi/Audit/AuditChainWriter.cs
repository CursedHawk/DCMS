using Dcms.Shared.Audit;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Audit;

/// <summary>
/// Drains <c>audit.audit_outbox</c> into the chain, then fans each record out to the AUDIT
/// stream for any external sink. The only writer of <c>audit.audit_events</c>.
///
/// <para><b>Why the claim query has no SKIP LOCKED.</b> ScheduledPublishWorker uses
/// <c>FOR UPDATE SKIP LOCKED</c>, which is right for independent work items and wrong here:
/// skipping a locked row would append records out of order and the chain's sequence would no
/// longer match the order they were produced in. Blocking is the point — contention naturally
/// holds concurrency at one writer, which is what a chain needs.</para>
///
/// <para><b>Why fan-out is separate from the append.</b> The database is the system of record;
/// the stream is a convenience for downstream consumers. Publishing after the append means a
/// NATS outage delays external sinks and nothing else — the record is already durable and
/// chained. <c>PublishedAt</c> tracks the fan-out independently of <c>AppliedAt</c> so a
/// publish failure never re-appends a record.</para>
/// </summary>
public sealed class AuditChainWriter(
    IServiceProvider services,
    IEventPublisher events,
    ILogger<AuditChainWriter> logger) : BackgroundService
{
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var drained = 0;
            try
            {
                drained = await DrainBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Loud, because a stalled writer means audit records exist but are not yet
                // chained or visible — a state that must not persist quietly.
                logger.LogError(ex, "Audit chain writer failed to drain the outbox; retrying.");
            }

            // A full batch usually means more is waiting; come straight back for it.
            if (drained < BatchSize)
            {
                await Task.Delay(IdlePollInterval, stoppingToken);
            }
        }
    }

    private async Task<int> DrainBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var appender = scope.ServiceProvider.GetRequiredService<AuditChainAppender>();
        var serializer = scope.ServiceProvider.GetRequiredService<AuditEventSerializer>();

        // Claim in insertion order and hold the rows for the whole batch. See the class remarks
        // on why SKIP LOCKED would be wrong here.
        await using var claim = await db.Database.BeginTransactionAsync(cancellationToken);

        var pending = await db.Outbox
            .FromSql($"""
                SELECT * FROM audit.audit_outbox
                WHERE "AppliedAt" IS NULL
                ORDER BY "Id"
                LIMIT {BatchSize}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            await claim.RollbackAsync(cancellationToken);
            return 0;
        }

        var decoded = new List<(AuditOutboxMessage Row, AuditEvent Event)>(pending.Count);
        foreach (var row in pending)
        {
            var @event = TryDecode(row, serializer);
            if (@event is null)
            {
                // Unparseable payload: mark it applied so it cannot block the chain forever,
                // but say so at Critical — a record we cannot read is a record we have lost.
                logger.LogCritical(
                    "Audit outbox row {Id} (event {EventId}) could not be deserialized and was skipped.",
                    row.Id, row.EventId);
                row.AppliedAt = DateTimeOffset.UtcNow;
                row.Attempts++;
                continue;
            }
            decoded.Add((row, @event));
        }

        if (decoded.Count > 0)
        {
            await appender.AppendAsync([.. decoded.Select(d => d.Event)], cancellationToken);

            var now = DateTimeOffset.UtcNow;
            foreach (var (row, _) in decoded)
            {
                row.AppliedAt = now;
                row.Attempts++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await claim.CommitAsync(cancellationToken);

        await FanOutAsync(db, decoded, cancellationToken);
        return pending.Count;
    }

    private static AuditEvent? TryDecode(AuditOutboxMessage row, AuditEventSerializer serializer)
    {
        try
        {
            return serializer.Deserialize(row.PayloadJson);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort publish to the AUDIT stream. Runs after the chain append has committed, so a
    /// failure here costs external sinks some freshness and costs the audit log nothing.
    /// </summary>
    private async Task FanOutAsync(
        AuditDbContext db,
        List<(AuditOutboxMessage Row, AuditEvent Event)> decoded,
        CancellationToken cancellationToken)
    {
        if (decoded.Count == 0)
        {
            return;
        }

        var published = 0;
        foreach (var (row, @event) in decoded)
        {
            try
            {
                await events.PublishAsync(
                    Subjects.AuditRecorded, @event, cancellationToken, @event.EventId.ToString());
                row.PublishedAt = DateTimeOffset.UtcNow;
                published++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Audit fan-out to the AUDIT stream failed for event {EventId}; the record is already chained.",
                    @event.EventId);
                break;
            }
        }

        if (published > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
