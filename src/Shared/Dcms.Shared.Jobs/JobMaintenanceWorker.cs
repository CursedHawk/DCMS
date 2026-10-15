using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Jobs;

/// <summary>
/// Keeps the ledger honest when the happy path did not finish (ADR 0024). Three sweeps, one
/// replica per pass:
///
/// <list type="bullet">
///   <item><b>Redispatch.</b> A job-worker job still Queued two minutes after its last change lost
///   its run request (NATS was down at enqueue, or the process died between commit and publish),
///   and one Running under a lapsed lease lost its worker. Both are sent again; the claim makes a
///   duplicate harmless. This is what lets the ledger row stand in for an outbox.</item>
///   <item><b>Silence.</b> A job on another queue (media, sites, flows) that has said nothing for
///   hours lost its worker mid-run with no one to report it. It is failed as stale rather than
///   left spinning in every viewer's progress ring forever.</item>
///   <item><b>Retention.</b> Finished jobs are deleted after <c>Jobs:RetentionDays</c> (30).</item>
/// </list>
/// </summary>
public sealed class JobMaintenanceWorker(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<JobMaintenanceWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RedispatchAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetentionEvery = TimeSpan.FromHours(1);
    private const int Batch = 200;

    private readonly TimeSpan _staleAfter = TimeSpan.FromMinutes(configuration.GetValue("Jobs:StaleAfterMinutes", 180));
    private readonly int _retentionDays = configuration.GetValue("Jobs:RetentionDays", 30);
    private DateTimeOffset _lastRetention = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Job maintenance pass failed; retrying next cycle.");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    public async Task PassAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }
        await using var leadership = await PostgresAdvisoryLock.TryAcquireAsync(
            connectionString, PostgresAdvisoryLock.JobMaintenanceLockKey, logger, ct);
        if (leadership is null)
        {
            return;
        }

        // Every tenant's rows by nature; each change narrows again through the ledger.
        using var rls = RlsScope.Platform();
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<JobsDbContext>();

        // Touching UpdatedAt and failing silent jobs are bookkeeping, not acts anyone needs in
        // the audit log; without this each would be a data.bulk.updated record every 30 seconds.
        using var quiet = sp.GetService<AuditScope>()?.SuppressBulkCapture();

        await RedispatchAsync(db, sp.GetRequiredService<IEventPublisher>(), ct);
        await FailSilentAsync(db, sp.GetRequiredService<JobLedger>(), ct);

        if (DateTimeOffset.UtcNow - _lastRetention >= RetentionEvery)
        {
            await RetireAsync(db, sp, ct);
            _lastRetention = DateTimeOffset.UtcNow;
        }
    }

    private async Task RedispatchAsync(JobsDbContext db, IEventPublisher events, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var queuedBefore = now - RedispatchAfter;
        var lost = await db.Jobs.IgnoreQueryFilters().AsNoTracking()
            .Where(j => (j.Lane == JobLanes.Tenant || j.Lane == JobLanes.Platform)
                        && ((j.Status == JobStatus.Queued && j.UpdatedAt < queuedBefore)
                            || (j.Status == JobStatus.Running && j.LeaseUntil < now)))
            .OrderBy(j => j.UpdatedAt)
            .Select(j => new { j.Id, j.TenantId, j.Kind, j.Lane })
            .Take(Batch)
            .ToListAsync(ct);

        foreach (var job in lost)
        {
            // Touch first, so a pass that dies after sending does not send again thirty seconds
            // later. Not a visible change, so no sequence bump and no announcement.
            await db.Jobs.IgnoreQueryFilters().Where(j => j.Id == job.Id && j.TenantId == job.TenantId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.UpdatedAt, now), ct);
            await JobDispatch.SendAsync(events, job.Id, job.TenantId, job.Kind, job.Lane,
                $"{job.Id}:r{now.UtcTicks}", logger, ct);
        }
        if (lost.Count > 0)
        {
            logger.LogInformation("Re-sent {Count} job run request(s) that had gone missing.", lost.Count);
        }
    }

    private async Task FailSilentAsync(JobsDbContext db, JobLedger ledger, CancellationToken ct)
    {
        var before = DateTimeOffset.UtcNow - _staleAfter;
        var silent = await db.Jobs.IgnoreQueryFilters().AsNoTracking()
            // Groups are excluded: they change only when a child does, and a child that went
            // silent is failed here itself, which then settles its group.
            .Where(j => j.Lane != JobLanes.Tenant && j.Lane != JobLanes.Platform && j.Lane != JobLanes.Group
                        && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
                        && j.UpdatedAt < before)
            .Select(j => new { j.Id, j.TenantId, j.Status })
            .Take(Batch)
            .ToListAsync(ct);

        foreach (var job in silent)
        {
            // FailAsync only leaves Running, and a Queued one never started: take it there first.
            if (job.Status == JobStatus.Queued)
            {
                using var _ = JobLedger.ScopeFor(job.TenantId);
                await db.Jobs.IgnoreQueryFilters()
                    .Where(j => j.Id == job.Id && j.TenantId == job.TenantId && j.Status == JobStatus.Queued)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running), ct);
            }
            await ledger.FailAsync(job.Id, job.TenantId, "jobs.error.stale",
                $"No word from its worker for {_staleAfter.TotalMinutes:0} minutes.", retryable: false, ct);
        }
        if (silent.Count > 0)
        {
            logger.LogWarning("Failed {Count} job(s) whose worker went silent.", silent.Count);
        }
    }

    private async Task RetireAsync(JobsDbContext db, IServiceProvider sp, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_retentionDays);

        // One audit record for the pass, as NotificationRetentionWorker does: a set-based delete
        // has no before-image to diff, and a record per row would bury what policy removed.
        var auditScope = sp.GetService<AuditScope>();
        var ambient = sp.GetService<AuditAmbient>();
        using var entered = auditScope is not null && ambient is not null ? ambient.Enter(auditScope) : null;
        using var suppressed = auditScope?.SuppressBulkCapture();

        var deleted = await db.Jobs.IgnoreQueryFilters()
            .Where(j => (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed || j.Status == JobStatus.Canceled)
                        && j.UpdatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            return;
        }

        if (sp.GetService<IAuditRecorder>() is { } recorder)
        {
            recorder.Record(AuditActions.JobsPruned)
                .Platform()
                .As(AuditCategory.TenantState)
                .For("jobs", "jobs", $"background jobs finished over {_retentionDays} days ago")
                .With("cutoff", cutoff.ToString("O"))
                .With("retentionDays", _retentionDays)
                .With("jobsDeleted", deleted);
            await recorder.FlushAsync(ct);
        }
        logger.LogInformation("Job retention removed {Deleted} finished job(s).", deleted);
    }
}
