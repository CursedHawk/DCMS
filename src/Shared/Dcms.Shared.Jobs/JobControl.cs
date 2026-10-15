using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Jobs;

/// <summary>
/// What a person can do to a job: cancel it or run it again. Shared by the tenant and platform
/// endpoints; authorization is theirs, this only acts. A group acts on its children.
/// </summary>
public sealed class JobControl(JobLedger ledger, JobsDbContext db, IEventPublisher events, ILogger<JobControl> logger)
{
    public async Task<CancelOutcome> CancelAsync(Job job, CancellationToken ct)
    {
        if (job.Lane != JobLanes.Group)
        {
            return await ledger.RequestCancelAsync(job.Id, job.TenantId, ct);
        }

        var outcomes = new List<CancelOutcome>();
        foreach (var child in await ChildrenAsync(job, j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running, ct))
        {
            outcomes.Add(await ledger.RequestCancelAsync(child, job.TenantId, ct));
        }
        return outcomes.Contains(CancelOutcome.Requested) ? CancelOutcome.Requested
            : outcomes.Contains(CancelOutcome.Canceled) ? CancelOutcome.Canceled
            : CancelOutcome.NotCancelable;
    }

    /// <summary>
    /// Runs a failed or canceled job again from its stored input. Only job-worker jobs: a job
    /// on another queue (a build, a transcode) is retried through its own feature, which knows
    /// what retrying it means.
    /// </summary>
    /// <returns>False when there was nothing this can retry.</returns>
    public async Task<bool> RetryAsync(Job job, CancellationToken ct)
    {
        if (job.Lane == JobLanes.Group)
        {
            var any = false;
            foreach (var child in await ChildrenAsync(job, j => j.Status == JobStatus.Failed || j.Status == JobStatus.Canceled, ct))
            {
                var row = await ledger.FindAsync(child, job.TenantId, ct);
                any |= row is not null && await RetryOneAsync(row, ct);
            }
            return any;
        }
        return await RetryOneAsync(job, ct);
    }

    private async Task<bool> RetryOneAsync(Job job, CancellationToken ct)
    {
        if (!JobDispatch.IsDispatched(job.Lane) || !await ledger.ResetForRetryAsync(job.Id, job.TenantId, ct))
        {
            return false;
        }
        // A message id of its own per retry: the original is still inside JetStream's dedupe
        // window for two minutes, and an immediate retry must not be discarded as its duplicate.
        await JobDispatch.SendAsync(events, job.Id, job.TenantId, job.Kind, job.Lane,
            $"{job.Id}:retry{DateTimeOffset.UtcNow.UtcTicks}", logger, ct);
        return true;
    }

    private async Task<List<Guid>> ChildrenAsync(Job parent, System.Linq.Expressions.Expression<Func<Job, bool>> filter, CancellationToken ct)
    {
        using var _ = JobLedger.ScopeFor(parent.TenantId);
        return await db.Jobs.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.ParentId == parent.Id && j.TenantId == parent.TenantId)
            .Where(filter)
            .Select(j => j.Id)
            .ToListAsync(ct);
    }
}
