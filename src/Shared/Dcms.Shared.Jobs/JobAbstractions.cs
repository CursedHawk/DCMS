using Dcms.Shared.Data.Jobs;

namespace Dcms.Shared.Jobs;

/// <summary>
/// What to run, as the caller describes it. Everything else on the ledger row (status,
/// attempts, actor, context) the framework fills in.
/// </summary>
/// <param name="Kind">The handler's registered kind, e.g. <c>media.drive-import</c>.</param>
/// <param name="TenantId">The tenant the work belongs to; null for platform work.</param>
/// <param name="Payload">The handler's input, serialised to JSON on the row (and retried from it).</param>
/// <param name="TitleKey">i18n key for the title the SPA shows.</param>
/// <param name="Params">Interpolation values for <paramref name="TitleKey"/>.</param>
/// <param name="RequiredPermission">The permission gating the feature; see <see cref="Job.RequiredPermission"/>.</param>
/// <param name="DedupeKey">Same key, same tenant: the enqueue returns the job already there.</param>
/// <param name="Lane">
/// <see cref="JobLanes.Tenant"/> or <see cref="JobLanes.Platform"/> to run it on a job worker.
/// Any other value names a queue of its own (media, sites, flows): the row is tracked here but no
/// run request is sent, because that queue's own producer already sent one.
/// </param>
/// <param name="Total">The amount of work, when known up front.</param>
/// <param name="Routine">Work nobody is waiting on; see <see cref="Job.Routine"/>.</param>
public sealed record JobSpec(
    string Kind,
    Guid? TenantId,
    object? Payload,
    string TitleKey,
    object? Params = null,
    string? RequiredPermission = null,
    string? DedupeKey = null,
    Guid? ParentId = null,
    Guid? Id = null,
    bool Cancelable = true,
    string Lane = JobLanes.Tenant,
    long? Total = null,
    string? LinkPath = null,
    int MaxAttempts = 3,
    bool Routine = false);

/// <param name="Existing">True when a job with the same dedupe key was already there and nothing was enqueued.</param>
public sealed record JobRef(Guid Id, bool Existing = false);

/// <summary>Records a job on the ledger and, for the job-worker lanes, asks a worker to run it.</summary>
public interface IJobQueue
{
    Task<JobRef> EnqueueAsync(JobSpec spec, CancellationToken ct);
}

/// <summary>
/// The running job, as its handler sees it.
///
/// <para><see cref="ReportAsync"/> is cheap to call as often as the work naturally ticks: it
/// writes at most once a second per job and never lets <c>done</c> go backwards, so report
/// overall progress (stages are labels over one count, not separate counts).</para>
/// </summary>
public interface IJobContext
{
    Guid JobId { get; }
    Guid? TenantId { get; }

    /// <summary>The group this job belongs to, if any: work it starts can join the same group.</summary>
    Guid? ParentId { get; }

    /// <summary>1 on the first run, 2 on the first retry, …</summary>
    int Attempt { get; }

    /// <summary>Fires when someone cancels the job, or when the host is stopping.</summary>
    CancellationToken Cancellation { get; }

    ValueTask ReportAsync(long done, long? total = null, string? stageKey = null);
}

/// <summary>
/// Runs one kind of job. Register with
/// <see cref="JobsServiceCollectionExtensions.AddJobHandler{THandler, TPayload}"/>. Resolved
/// from a fresh scope per run, inside the job's tenant (database and audit) and actor.
/// </summary>
public interface IJobHandler<in TPayload>
{
    /// <returns>A result to store on the row (serialised to JSON), or null.</returns>
    Task<object?> RunAsync(TPayload payload, IJobContext context);
}

/// <summary>
/// A failure that retrying will not fix: bad input, a missing resource, an expired grant. The
/// job fails at once with <see cref="ErrorKey"/> (an i18n key) rather than spending its
/// attempts. Any other exception is retried.
/// </summary>
public sealed class JobFailedException(string errorKey, string? detail = null, Exception? inner = null)
    : Exception(detail ?? errorKey, inner)
{
    public string ErrorKey { get; } = errorKey;
    public string? Detail { get; } = detail;
}
