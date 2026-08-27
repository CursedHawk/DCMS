using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Data.Analytics;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Analytics;

/// <summary>
/// Deletes raw analytics events older than the retention window, keeping the daily rollups
/// forever.
///
/// <para><b>Why this exists.</b> <c>analytics.events</c> had no retention at all. Every
/// pageview from every tenant site accumulated indefinitely on a host with 40 GB free, and
/// nothing in the platform would have said so until Postgres could not write. The rollups next
/// to it already hold the aggregate — that is what <c>daily_rollups</c> is for — so keeping raw
/// rows past the window the dashboards query buys precision nobody asks for at a cost that
/// compounds daily.</para>
///
/// <para><b>What is kept.</b> Rollups, permanently. The visitor-traffic view reads from those,
/// which is why the analytics dashboards still answer questions about last year while raw
/// events are pruned at 90 days. Dimensional breakdowns — country, device, browser, UTM — come
/// from raw events and therefore stop at the window; that is the trade, and it is stated on the
/// dashboard.</para>
///
/// <para><b>Deleted in bounded batches, not one statement.</b> A single
/// <c>DELETE FROM analytics.events WHERE "OccurredAt" &lt; …</c> on a first run against years
/// of rows would hold one transaction open across the whole table, block the ingest consumer
/// behind it, and generate more WAL than the disk this is protecting can spare. Ten thousand
/// rows at a time, with a ceiling per pass, means the first run takes several days to catch up
/// and never once holds a long transaction.</para>
///
/// <para><b>What the audit log gets.</b> A set-based delete leaves no before-image, and there
/// will not be one — reading ten thousand rows purely to log that they were deleted would
/// double the cost of the job that exists to protect the disk. So the per-statement capture is
/// suppressed and one <c>analytics.retention.pruned</c> record is written for the whole pass,
/// carrying the policy, the cutoff and the count. Twenty records saying "a table got shorter"
/// is strictly less informative than one saying what removed how much and why, and the batching
/// is an implementation detail of not holding a long transaction.</para>
/// </summary>
public sealed class AnalyticsRetentionWorker(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<AnalyticsRetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>
    /// One statement's worth. Small enough that the lock it takes is uninteresting to the
    /// ingest consumer running alongside it.
    /// </summary>
    private const int BatchSize = 10_000;

    /// <summary>
    /// Ceiling per pass. Caps the WAL a single run can generate, so catching up on a large
    /// backlog is spread across passes instead of landing as one disk-filling burst — which
    /// would be an ironic way for a disk-protection job to take the host down.
    /// </summary>
    private const int MaxBatchesPerPass = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short delay rather than running at once: startup already runs every migration, the
        // RLS configurator, the view configurator and the audit maintenance pass, and adding a
        // bulk delete to that is how a deploy times out its own health check.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

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
                // Never fatal. Analytics retention failing is a disk that fills more slowly
                // than it otherwise would; taking admin-api down over it would stop the
                // platform.
                logger.LogError(ex, "Analytics retention pass failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var days = configuration.GetValue("Analytics:RetentionDays", 90);
        if (days <= 0)
        {
            // An explicit opt-out, for an installation that has a reason to keep everything.
            // Logged once per pass so that "why is the events table enormous" has an answer.
            logger.LogInformation("Analytics retention is disabled (Analytics:RetentionDays <= 0).");
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();
        var auditScope = scope.ServiceProvider.GetRequiredService<AuditScope>();
        var ambient = scope.ServiceProvider.GetRequiredService<AuditAmbient>();
        using var ambientScope = ambient.Enter(auditScope);

        // One record for the pass instead of one per batch. See the class remarks.
        using var bulkSuppressed = auditScope.SuppressBulkCapture();

        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerPass && !ct.IsCancellationRequested; batch++)
        {
            // IgnoreQueryFilters because retention is cross-tenant by nature and this worker
            // has no ambient tenant — the same reason the site builder ignores them.
            var deleted = await db.Events
                .IgnoreQueryFilters()
                .Where(e => e.OccurredAt < cutoff)
                .OrderBy(e => e.Id)
                .Take(BatchSize)
                .ExecuteDeleteAsync(ct);

            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            recorder.Record(AuditActions.AnalyticsPruned)
                .Platform()
                .As(AuditCategory.TenantState)
                .For("analytics", "events", $"raw events older than {days} days")
                .With("cutoff", cutoff.ToString("O"))
                .With("retentionDays", days)
                .With("rowsDeleted", total);

            await recorder.FlushAsync(ct);

            logger.LogInformation(
                "Analytics retention removed {Rows} raw event(s) older than {Days} days. Daily rollups are unaffected and are kept indefinitely.",
                total,
                days);
        }
    }
}
