using System.Text.Json;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Messaging;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Cms;

/// <summary>
/// Relays content_outbox rows to NATS at-least-once, then stamps SentAt. Polls
/// every 2s; resilient to NATS/DB being unavailable. Consumers are idempotent.
///
/// <para>Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>, which is what makes running more
/// than one admin-api replica safe. A plain <c>WHERE SentAt IS NULL</c> read gives every replica
/// the same rows: each publishes all of them, and the duplicate NATS messages are real -- "the
/// consumers are idempotent" covers a JetStream redelivery of one message, not N independent
/// publishes of N distinct messages that happen to carry the same payload.</para>
/// </summary>
public sealed class OutboxDispatcher(
    IServiceProvider services,
    IEventPublisher events,
    AuditAmbient ambient,
    DcmsMetrics metrics,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Must match the LIMIT in <see cref="ClaimSql"/>.</summary>
    private const int BatchSize = 100;

    /// <summary>
    /// Claims a batch for this replica. Columns are EF's default PascalCase identifiers (only the
    /// table name is snake_cased), so they must be double-quoted in raw SQL. Same shape as
    /// <c>ScheduledPublishWorker</c>, which is the worker this one should always have matched.
    /// </summary>
    private const string ClaimSql =
        """
        SELECT * FROM cms.content_outbox
        WHERE "SentAt" IS NULL
        ORDER BY "OccurredAt"
        FOR UPDATE SKIP LOCKED
        LIMIT 100
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Outbox dispatch failed; retrying.");
            }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task DispatchBatchAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsDbContext>();

        // The claim, the publish and the SentAt stamp are one transaction: the row locks have to
        // outlive the publish, or a sibling replica could claim a row this one has read but not
        // yet marked sent.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var pending = await db.Outbox.FromSqlRaw(ClaimSql).ToListAsync(ct);

        // Reported on every poll, including the polls that find nothing — a depth that simply
        // stops being reported holds its last value on the dashboard, so a dispatcher that has
        // died and one that has caught up would look identical. Counted rather than inferred
        // from the batch: the batch is capped at BatchSize, so a backlog of ten thousand and a
        // backlog of a hundred both fill it and neither is visible from the page alone. Only
        // when the page is full, because that is the only time the cheap answer is wrong.
        //
        // Note this counts unsent rows regardless of who holds them, so with several replicas
        // draining concurrently the depth is the platform's backlog rather than this replica's
        // share — which is the number the dashboard wants.
        metrics.OutboxDepth("cms.content_outbox", pending.Count < BatchSize
            ? pending.Count
            : await db.Outbox.CountAsync(o => o.SentAt == null, ct));

        foreach (var message in pending)
        {
            // Enqueue-to-dispatch latency, which is the number the content pipeline is judged
            // on: everything downstream of here is JetStream's problem, everything upstream is
            // the request's. Recorded before publishing, so a slow NATS shows up as message
            // handling time rather than inflating the queue wait it did not cause.
            metrics.OutboxLag("cms.content_outbox", DateTimeOffset.UtcNow - message.OccurredAt);

            // Put back the context of the request that enqueued this row, so the headers the
            // publisher stamps name that person rather than this two-second timer. Per message,
            // and disposed before the next: two rows in one batch may come from two people.
            var restored = new AuditScope();
            AuditPropagation.Restore(restored, AuditPropagation.FromJson(message.ContextJson), message.TenantId);
            using (ambient.Enter(restored))
            {
                await PublishAsync(message, ct);
            }

            message.SentAt = DateTimeOffset.UtcNow;
            message.Attempts++;
        }

        if (pending.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        // Commits either way: an empty batch still opened a transaction, and leaving it open
        // would pin the oldest snapshot and hold back vacuum on a two-second timer.
        await tx.CommitAsync(ct);
    }

    private async ValueTask PublishAsync(ContentOutboxMessage message, CancellationToken ct)
    {
        switch (message.Subject)
        {
            case Subjects.ContentPublished:
                await events.PublishAsync(message.Subject,
                    JsonSerializer.Deserialize<ContentPublished>(message.PayloadJson)!, ct);
                break;
            case Subjects.ContentUnpublished:
                await events.PublishAsync(message.Subject,
                    JsonSerializer.Deserialize<ContentUnpublished>(message.PayloadJson)!, ct);
                break;
            default:
                logger.LogWarning("Unknown outbox subject {Subject}; skipping.", message.Subject);
                break;
        }
    }
}
