using Dcms.Shared.Audit;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Audit;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Audit;

/// <summary>
/// The audit log's housekeeping: seal finished months, keep partitions ahead of the writer,
/// drop what retention has expired, and publish the numbers that say whether any of it is
/// working.
///
/// <para>Runs hourly rather than on a month-boundary schedule. A job that fires once a month is
/// a job nobody notices has stopped, and one that has to be running at midnight on the first is
/// a job that misses a month whenever a deploy lands badly. Every step is idempotent, so the
/// cost of running it sixty times too often is sixty cheap no-ops.</para>
///
/// <para>It also runs once at startup, before the first hour elapses: a service that has been
/// down across a month boundary needs this month's partition to exist before it takes a
/// request, not an hour later.</para>
///
/// <para>Exactly one replica runs a given pass, elected by a Postgres advisory lock. Every step
/// is idempotent, so concurrent passes would not corrupt anything -- but they would each do the
/// same DDL and each publish the same gauges, and a metric written N times by N replicas is not
/// N times more informative.</para>
/// </summary>
public sealed class AuditMaintenanceWorker(
    IServiceProvider services,
    IConfiguration configuration,
    AuditMetrics metrics,
    ILogger<AuditMaintenanceWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>How far back the omission check looks — several passes' worth, on purpose.</summary>
    private static readonly TimeSpan LookBack = TimeSpan.FromHours(6);

    /// <summary>
    /// How far short of the present the omission check stops. A process that has taken a
    /// sequence number but not yet flushed is not a lost record, and alerting on one would
    /// teach an operator to ignore the alert.
    /// </summary>
    private static readonly TimeSpan SettlePeriod = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit maintenance pass failed; retrying next interval.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("ConnectionStrings:Postgres is not configured; skipping audit maintenance.");
            return;
        }

        // Whichever replica gets here first does this pass; the others skip it and try again
        // next interval. Not an error, and not worth a warning -- it is the design working.
        await using var leadership = await PostgresAdvisoryLock.TryAcquireAsync(
            connectionString, PostgresAdvisoryLock.AuditMaintenanceLockKey, logger, ct);

        if (leadership is null)
        {
            logger.LogDebug("Another replica is running audit maintenance; skipping this pass.");
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        // Partitions first: the writer needs somewhere to put today's records, and that has to
        // be true before anything else in this pass matters.
        await AuditSchemaConfigurator.EnsurePartitionsAsync(db, logger, ct);
        // A partition created here is brand new, so it needs its chain index too — the parent
        // cannot carry that one, see EnsureChainIndexesAsync.
        await AuditSchemaConfigurator.EnsureChainIndexesAsync(db, logger, ct);

        var retention = scope.ServiceProvider.GetRequiredService<AuditRetention>();
        await retention.ApplyAsync(ct);

        await ObserveAsync(db, ct);
        await CheckForOmissionsAsync(
            scope.ServiceProvider.GetRequiredService<AuditGapDetector>(), ct);
    }

    /// <summary>
    /// Publishes what an operator needs to know, and says the alarming parts out loud.
    ///
    /// <para>An audit log that has quietly stopped writing looks exactly like a platform where
    /// nobody did anything. That is the failure this exists to make impossible to miss.</para>
    /// </summary>
    private async Task ObserveAsync(AuditDbContext db, CancellationToken ct)
    {
        var depth = await db.Outbox.CountAsync(o => o.AppliedAt == null, ct);
        metrics.RecordOutboxDepth(depth);

        var oldest = await db.Outbox
            .Where(o => o.AppliedAt == null)
            .OrderBy(o => o.Id)
            .Select(o => (DateTimeOffset?)o.OccurredAt)
            .FirstOrDefaultAsync(ct);

        var lag = oldest is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - oldest.Value;
        metrics.RecordWriterLag(lag);

        // A backlog this old is not a slow writer; it is a stopped one. The threshold is
        // deliberately generous — the writer polls every two seconds — so crossing it means
        // something is actually wrong rather than merely busy.
        if (lag > TimeSpan.FromMinutes(15))
        {
            logger.LogCritical(
                "Audit writer is {Minutes:F0} minutes behind with {Depth} record(s) unchained. "
                + "Committed changes are not reaching the audit log.",
                lag.TotalMinutes, depth);
        }
    }

    /// <summary>
    /// Looks for records that were begun and never arrived.
    ///
    /// <para>This is the check the hash chain cannot perform. A chain proves the surviving
    /// records were not altered or reordered; a chain over records that were silently dropped
    /// verifies perfectly. The per-process sequence is what turns an omission into a number,
    /// and this is where that number becomes an alert.</para>
    /// </summary>
    private async Task CheckForOmissionsAsync(AuditGapDetector detector, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // The window overlaps the previous pass several times over, deliberately: a gap that
        // appears once must not be able to fall between two hourly runs, and re-counting a
        // gap that is still there is the correct behaviour for something this serious.
        var gaps = await detector.FindAsync(now - LookBack, now - SettlePeriod, ct);

        foreach (var gap in gaps)
        {
            metrics.RecordProducerGap(gap.Missing);
        }
    }
}
