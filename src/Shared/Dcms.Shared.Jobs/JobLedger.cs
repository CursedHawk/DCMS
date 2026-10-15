using Dcms.Shared.Audit;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dcms.Shared.Jobs;

public enum CancelOutcome
{
    NotFound,

    /// <summary>It was queued and is now canceled.</summary>
    Canceled,

    /// <summary>It is running; the worker has been asked and will stop at its next check.</summary>
    Requested,

    /// <summary>It already finished, or it is running and its kind cannot stop part-way.</summary>
    NotCancelable,
}

/// <summary>
/// Every write the ledger takes (ADR 0024). Each state transition is one conditional
/// <c>UPDATE</c> whose <c>WHERE</c> names the states it may leave, so two replicas racing for the
/// same job settle it in Postgres rather than in a check-then-write that both pass.
///
/// <para><b>Scoped by the job's own tenant.</b> Workers have no ambient tenant, so every call
/// takes the job's tenant and runs under <see cref="RlsScope.Tenant"/> (or
/// <see cref="RlsScope.Platform"/> for a platform job) with the EF filter removed. The database
/// still confines each statement to that one tenant; the id is the predicate.</para>
///
/// <para><b>Not audited statement by statement.</b> Every write here is a set-based update, which
/// the bulk interceptor would otherwise record as <c>data.bulk.updated</c> — once per progress
/// tick. The ledger is the mechanics of work that is audited where it is asked for (the import,
/// the publish, a cancel), so each statement runs with bulk capture suppressed.</para>
///
/// <para>Every change is announced on <c>jobs.changed</c> after it lands. The announcement is a
/// hint for browsers: best-effort, after the write, and never allowed to fail it.</para>
/// </summary>
public sealed class JobLedger(
    JobsDbContext db, IEventPublisher events, ILogger<JobLedger> logger, AuditScope? audit = null)
{
    /// <summary>How long a claim holds without renewal. A worker renews every few seconds.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public static IDisposable ScopeFor(Guid? tenantId) =>
        tenantId is { } tenant ? RlsScope.Tenant(tenant) : RlsScope.Platform();

    /// <summary>The job's tenant for the database, and no per-statement audit (see the class remarks).</summary>
    private IDisposable Enter(Guid? tenantId)
    {
        var rls = ScopeFor(tenantId);
        var quiet = audit?.SuppressBulkCapture();
        return new Both(rls, quiet);
    }

    private sealed class Both(IDisposable first, IDisposable? second) : IDisposable
    {
        public void Dispose()
        {
            second?.Dispose();
            first.Dispose();
        }
    }

    /// <summary>
    /// One job, by id <b>and</b> tenant. The tenant is part of the predicate, not only of the RLS
    /// scope: the owner connection bypasses row security, and a caller naming another tenant's
    /// job id must find nothing either way. A null tenant matches only platform jobs, so a
    /// tenant-less caller cannot reach a tenant's row by passing no tenant.
    /// </summary>
    private IQueryable<Job> Row(Guid id, Guid? tenantId) =>
        db.Jobs.IgnoreQueryFilters().Where(j => j.Id == id && j.TenantId == tenantId);

    public async Task<Job?> FindAsync(Guid id, Guid? tenantId, CancellationToken ct)
    {
        using var _ = Enter(tenantId);
        return await Row(id, tenantId).AsNoTracking().FirstOrDefaultAsync(ct);
    }

    /// <summary>Inserts the row; a dedupe-key collision returns the job already there.</summary>
    public async Task<JobRef> CreateAsync(Job job, CancellationToken ct)
    {
        using var _ = Enter(job.TenantId);
        db.Jobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (job.DedupeKey is not null
                                           && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(job).State = EntityState.Detached;
            var existing = await db.Jobs.IgnoreQueryFilters().AsNoTracking()
                .Where(j => j.TenantId == job.TenantId && j.DedupeKey == job.DedupeKey)
                .Select(j => j.Id)
                .FirstAsync(ct);
            return new JobRef(existing, Existing: true);
        }
        db.Entry(job).State = EntityState.Detached;
        await AnnounceAsync(job.Id, job.TenantId, ct);
        return new JobRef(job.Id);
    }

    /// <summary>
    /// Takes a job to run it: Queued, or Running under a lease that lapsed (its worker died).
    /// Null when someone else holds it or it already finished, which is how a duplicate or a
    /// redelivered run message is told apart from work.
    /// </summary>
    public async Task<Job?> ClaimAsync(Guid id, Guid? tenantId, CancellationToken ct)
    {
        using var _ = Enter(tenantId);
        var now = DateTimeOffset.UtcNow;
        var claimed = await Row(id, tenantId)
            .Where(j => j.CancelRequestedAt == null
                        && (j.Status == JobStatus.Queued || (j.Status == JobStatus.Running && j.LeaseUntil < now)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.LeaseUntil, now + Lease)
                .SetProperty(j => j.StartedAt, j => j.StartedAt ?? now)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                .SetProperty(j => j.Seq, j => j.Seq + 1)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (claimed == 0)
        {
            return null;
        }
        await AnnounceAsync(id, tenantId, ct);
        return await Row(id, tenantId).AsNoTracking().FirstAsync(ct);
    }

    /// <summary>
    /// Renews the worker's lease, and answers whether it should stop: true when the job was
    /// canceled, or is no longer running at all (taken over, or failed by someone else).
    /// Not announced: nothing a person can see changed.
    /// </summary>
    public async Task<bool> RenewAsync(Guid id, Guid? tenantId, CancellationToken ct)
    {
        using var _ = Enter(tenantId);
        var now = DateTimeOffset.UtcNow;
        var renewed = await Row(id, tenantId)
            .Where(j => j.Status == JobStatus.Running && j.CancelRequestedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, now + Lease), ct);
        return renewed == 0;
    }

    /// <summary>Progress on a running job. <paramref name="done"/> never moves the count backwards.</summary>
    public async Task ProgressAsync(Guid id, Guid? tenantId, long done, long? total, string? stageKey, CancellationToken ct)
    {
        using var _ = Enter(tenantId);
        var now = DateTimeOffset.UtcNow;
        var updated = await Row(id, tenantId)
            .Where(j => j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s =>
            {
                s.SetProperty(j => j.Done, j => j.Done > done ? j.Done : done);
                if (total is not null)
                {
                    s.SetProperty(j => j.Total, total);
                }
                if (stageKey is not null)
                {
                    s.SetProperty(j => j.StageKey, stageKey);
                }
                s.SetProperty(j => j.Seq, j => j.Seq + 1);
                s.SetProperty(j => j.UpdatedAt, now);
            }, ct);
        if (updated > 0)
        {
            await AnnounceAsync(id, tenantId, ct);
        }
    }

    public async Task<bool> CompleteAsync(Guid id, Guid? tenantId, string? resultJson, CancellationToken ct)
    {
        using var _ = Enter(tenantId);
        var now = DateTimeOffset.UtcNow;
        var updated = await Row(id, tenantId)
            .Where(j => j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Succeeded)
                // A finished job reads as whole even when the handler never reported the last tick.
                .SetProperty(j => j.Done, j => j.Total ?? (j.Done > 0 ? j.Done : 1))
                .SetProperty(j => j.Total, j => j.Total ?? (j.Done > 0 ? j.Done : 1))
                .SetProperty(j => j.ResultJson, resultJson)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.Seq, j => j.Seq + 1)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (updated > 0)
        {
            await AnnounceAsync(id, tenantId, ct);
        }
        return updated > 0;
    }

    /// <summary>
    /// Records a failed run. When the failure is worth retrying and attempts remain, the job goes
    /// back to Queued (the caller re-delivers it after a backoff); otherwise it is Failed.
    /// </summary>
    /// <returns>The status it ended in, or null when it was not running.</returns>
    public async Task<JobStatus?> FailAsync(
        Guid id, Guid? tenantId, string errorKey, string? detail, bool retryable, CancellationToken ct)
    {
        using var _ = Enter(tenantId);
        var now = DateTimeOffset.UtcNow;
        detail = Truncate(detail);

        if (retryable)
        {
            var requeued = await Row(id, tenantId)
                .Where(j => j.Status == JobStatus.Running && j.Attempts < j.MaxAttempts && j.CancelRequestedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Queued)
                    .SetProperty(j => j.ErrorKey, errorKey)
                    .SetProperty(j => j.ErrorDetail, detail)
                    .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(j => j.Seq, j => j.Seq + 1)
                    .SetProperty(j => j.UpdatedAt, now), ct);
            if (requeued > 0)
            {
                await AnnounceAsync(id, tenantId, ct);
                return JobStatus.Queued;
            }
        }

        var failed = await Row(id, tenantId)
            .Where(j => j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.ErrorKey, errorKey)
                .SetProperty(j => j.ErrorDetail, detail)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.Seq, j => j.Seq + 1)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (failed == 0)
        {
            return null;
        }
        await AnnounceAsync(id, tenantId, ct);
        return JobStatus.Failed;
    }

    /// <summary>The host is stopping mid-run: hand the job straight back rather than waiting out the lease.</summary>
    public async Task ReleaseAsync(Guid id, Guid? tenantId, CancellationToken ct)
    {
        using var _ = Enter(tenantId);
        var now = DateTimeOffset.UtcNow;
        // The attempt did not fail, so it is not spent either.
        await Row(id, tenantId)
            .Where(j => j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Queued)
                .SetProperty(j => j.Attempts, j => j.Attempts > 0 ? j.Attempts - 1 : 0)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.Seq, j => j.Seq + 1)
                .SetProperty(j => j.UpdatedAt, now), ct);
        await AnnounceAsync(id, tenantId, ct);
    }

    public async Task<CancelOutcome> RequestCancelAsync(Guid id, Guid? tenantId, CancellationToken ct)
    {
        using var _ = Enter(tenantId);
        var now = DateTimeOffset.UtcNow;

        // Queued: nothing has started, so it simply never will. The run message still in the
        // queue finds a canceled row and is dropped by the claim.
        var canceled = await Row(id, tenantId)
            .Where(j => j.Status == JobStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Canceled)
                .SetProperty(j => j.CancelRequestedAt, now)
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.Seq, j => j.Seq + 1)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (canceled > 0)
        {
            await AnnounceAsync(id, tenantId, ct);
            return CancelOutcome.Canceled;
        }

        // Running: ask. The worker sees it at its next lease renewal and stops cooperatively;
        // only it can say when the work has actually stopped.
        var requested = await Row(id, tenantId)
            .Where(j => j.Status == JobStatus.Running && j.Cancelable && j.CancelRequestedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.CancelRequestedAt, now)
                .SetProperty(j => j.Seq, j => j.Seq + 1)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (requested > 0)
        {
            await AnnounceAsync(id, tenantId, ct);
            return CancelOutcome.Requested;
        }

        return await Row(id, tenantId).AnyAsync(ct) ? CancelOutcome.NotCancelable : CancelOutcome.NotFound;
    }

    /// <summary>The worker stopped because it was asked to.</summary>
    public async Task MarkCanceledAsync(Guid id, Guid? tenantId, CancellationToken ct)
    {
        using var _ = Enter(tenantId);
        var now = DateTimeOffset.UtcNow;
        var updated = await Row(id, tenantId)
            .Where(j => j.Status == JobStatus.Running || j.Status == JobStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Canceled)
                .SetProperty(j => j.CancelRequestedAt, j => j.CancelRequestedAt ?? now)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.Seq, j => j.Seq + 1)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (updated > 0)
        {
            await AnnounceAsync(id, tenantId, ct);
        }
    }

    /// <summary>
    /// A failed or canceled job, queued again from scratch with its original input. The caller
    /// sends the run request; this only resets the row.
    /// </summary>
    public async Task<bool> ResetForRetryAsync(Guid id, Guid? tenantId, CancellationToken ct)
    {
        using var _ = Enter(tenantId);
        var now = DateTimeOffset.UtcNow;
        var updated = await Row(id, tenantId)
            .Where(j => j.Status == JobStatus.Failed || j.Status == JobStatus.Canceled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Queued)
                .SetProperty(j => j.Attempts, 0)
                .SetProperty(j => j.Done, 0)
                .SetProperty(j => j.StageKey, (string?)null)
                .SetProperty(j => j.ErrorKey, (string?)null)
                .SetProperty(j => j.ErrorDetail, (string?)null)
                .SetProperty(j => j.ResultJson, (string?)null)
                .SetProperty(j => j.CancelRequestedAt, (DateTimeOffset?)null)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.StartedAt, (DateTimeOffset?)null)
                .SetProperty(j => j.FinishedAt, (DateTimeOffset?)null)
                .SetProperty(j => j.Seq, j => j.Seq + 1)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (updated > 0)
        {
            await AnnounceAsync(id, tenantId, ct);
        }
        return updated > 0;
    }

    /// <summary>
    /// Folds a report from a bus-only worker into the row. Two rules make arrival order matter
    /// little: a finished job never changes again, and the count never goes backwards.
    /// </summary>
    public async Task ApplyReportAsync(JobStateReported report, CancellationToken ct)
    {
        // Reports speak for the queues that own their work (media, sites, flows). A job-worker
        // job has a worker that writes the ledger itself, and nothing on the bus gets to
        // overrule it -- whoever can publish jobs.state cannot finish or fail one.
        using (Enter(report.TenantId))
        {
            var lane = await Row(report.JobId, report.TenantId).Select(j => j.Lane).FirstOrDefaultAsync(ct);
            if (lane is null || JobDispatch.IsDispatched(lane))
            {
                logger.LogWarning("Ignoring a bus report for job {JobId}: no such job in tenant {TenantId} on a reporting queue.",
                    report.JobId, report.TenantId);
                return;
            }
        }

        var status = report.Status is null ? (JobStatus?)null : Enum.Parse<JobStatus>(report.Status);
        switch (status)
        {
            case JobStatus.Succeeded:
                await StartIfQueuedAsync(report.JobId, report.TenantId, ct);
                if (report.Done is not null || report.Total is not null)
                {
                    await ProgressAsync(report.JobId, report.TenantId, report.Done ?? 0, report.Total, report.StageKey, ct);
                }
                await CompleteAsync(report.JobId, report.TenantId, report.ResultJson, ct);
                return;
            case JobStatus.Failed:
                await StartIfQueuedAsync(report.JobId, report.TenantId, ct);
                await FailAsync(report.JobId, report.TenantId, report.ErrorKey ?? "jobs.error.failed", report.ErrorDetail,
                    retryable: false, ct);
                return;
            case JobStatus.Canceled:
                await MarkCanceledAsync(report.JobId, report.TenantId, ct);
                return;
            default:
                await StartIfQueuedAsync(report.JobId, report.TenantId, ct);
                if (report.Done is not null || report.Total is not null || report.StageKey is not null)
                {
                    await ProgressAsync(report.JobId, report.TenantId, report.Done ?? 0, report.Total, report.StageKey, ct);
                }
                return;
        }
    }

    /// <summary>A bus-only worker started on it. Its own queue holds the lease, so none is set here.</summary>
    private async Task StartIfQueuedAsync(Guid id, Guid? tenantId, CancellationToken ct)
    {
        using var _ = Enter(tenantId);
        var now = DateTimeOffset.UtcNow;
        var started = await Row(id, tenantId)
            .Where(j => j.Status == JobStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.StartedAt, j => j.StartedAt ?? now)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                .SetProperty(j => j.Seq, j => j.Seq + 1)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (started > 0)
        {
            await AnnounceAsync(id, tenantId, ct);
        }
    }

    /// <summary>
    /// Publishes <c>jobs.changed</c> for the row as it now stands and, for a child, brings its
    /// parent up to date. Never throws: the row is the record, this is a hint about it (ADR 0013),
    /// and a broken NATS must not fail the work it would have announced.
    /// </summary>
    public async Task AnnounceAsync(Guid id, Guid? tenantId, CancellationToken ct)
    {
        try
        {
            var parentId = await PublishChangedAsync(id, tenantId, ct);
            if (parentId is { } parent)
            {
                await RollUpAsync(parent, tenantId, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not announce job {JobId}; browsers will catch up on their next fetch.", id);
        }
    }

    /// <returns>The row's parent, if it has one.</returns>
    private async Task<Guid?> PublishChangedAsync(Guid id, Guid? tenantId, CancellationToken ct)
    {
        using var _ = Enter(tenantId);
        var row = await Row(id, tenantId).AsNoTracking()
            .Select(j => new { j.TenantId, j.ParentId, j.Status, j.Done, j.Total, j.StageKey, j.Seq })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }
        try
        {
            await events.PublishAsync(Subjects.JobChanged, new JobChanged(
                Guid.NewGuid(), DateTimeOffset.UtcNow, id, row.TenantId, row.ParentId,
                row.Status.ToString(), row.Done, row.Total, row.StageKey, row.Seq), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not announce job {JobId}.", id);
        }
        return row.ParentId;
    }

    /// <summary>
    /// A group job (<see cref="JobLanes.Group"/>) is its children: its progress is their mean, it
    /// is running while any of them is unfinished, and it ends Failed if any failed. Recomputed
    /// from the children on every child change rather than incremented, so two children
    /// finishing at once converge on the right answer whichever update lands last.
    /// </summary>
    private async Task RollUpAsync(Guid parentId, Guid? tenantId, CancellationToken ct)
    {
        using var _ = Enter(tenantId);
        var agg = await db.Jobs.IgnoreQueryFilters()
            .Where(c => c.ParentId == parentId && c.TenantId == tenantId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Unfinished = g.Count(c => c.Status == JobStatus.Queued || c.Status == JobStatus.Running),
                Started = g.Count(c => c.Status != JobStatus.Queued),
                Failed = g.Count(c => c.Status == JobStatus.Failed),
                Canceled = g.Count(c => c.Status == JobStatus.Canceled),
                // Each child counts 0..100: whole once finished, its own fraction while running.
                Progress = g.Sum(c => c.Status == JobStatus.Succeeded || c.Status == JobStatus.Failed || c.Status == JobStatus.Canceled
                    ? 100L
                    : (c.Total ?? 0) > 0 ? c.Done * 100 / (c.Total ?? 1) : 0L),
            })
            .FirstOrDefaultAsync(ct);
        if (agg is null)
        {
            return;
        }

        var status = agg.Unfinished > 0
            ? agg.Started > 0 ? JobStatus.Running : JobStatus.Queued
            : agg.Failed > 0 ? JobStatus.Failed
            : agg.Canceled == agg.Count ? JobStatus.Canceled
            : JobStatus.Succeeded;
        var finished = agg.Unfinished == 0;
        var now = DateTimeOffset.UtcNow;

        var updated = await Row(parentId, tenantId)
            .Where(j => j.Lane == JobLanes.Group)
            .ExecuteUpdateAsync(s =>
            {
                s.SetProperty(j => j.Status, status);
                s.SetProperty(j => j.Total, agg.Count * 100L);
                s.SetProperty(j => j.Done, agg.Progress);
                if (status != JobStatus.Queued)
                {
                    s.SetProperty(j => j.StartedAt, j => j.StartedAt ?? now);
                }
                s.SetProperty(j => j.FinishedAt, finished ? now : null);
                s.SetProperty(j => j.ErrorKey, finished && agg.Failed > 0 ? "jobs.error.someFailed" : null);
                s.SetProperty(j => j.Seq, j => j.Seq + 1);
                s.SetProperty(j => j.UpdatedAt, now);
            }, ct);
        if (updated > 0)
        {
            await PublishChangedAsync(parentId, tenantId, ct);
        }
    }

    private static string? Truncate(string? detail) =>
        detail is { Length: > 4000 } ? detail[..4000] : detail;
}
