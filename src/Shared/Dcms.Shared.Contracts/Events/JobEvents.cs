namespace Dcms.Shared.Contracts.Events;

/// <summary>
/// Run one job, on <c>jobs.run.{lane}.{kind}</c> (work queue). Deliberately just a pointer: the
/// payload, the actor and the attempt count live on the ledger row, which the worker claims
/// before doing anything, so a duplicate or stale message can never run the job twice.
/// </summary>
public sealed record JobRunRequested(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid JobId,
    Guid? TenantId,
    string Kind) : IDcmsEvent
{
    public int Version => 1;
}

/// <summary>
/// A ledger row changed, on <c>jobs.changed</c>. Pushed to browsers as it is, so it carries no
/// title, parameters or payload: nothing the recipient's next authorised request would not tell
/// them anyway (the same reasoning as <c>ResourceChanged</c>).
/// </summary>
/// <param name="Status">The <c>JobStatus</c> name.</param>
/// <param name="Seq">The row's write sequence; a browser ignores anything older than what it holds.</param>
public sealed record JobChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid JobId,
    Guid? TenantId,
    Guid? ParentId,
    string Status,
    long Done,
    long? Total,
    string? StageKey,
    long Seq) : IDcmsEvent
{
    public int Version => 1;
}

/// <summary>
/// Progress or an outcome from a worker that holds no grant on the jobs schema (media-worker,
/// site-builder), on <c>jobs.state</c>. admin-api folds it into the ledger. Applied with two
/// rules that make order not matter much: a finished job never changes again, and
/// <see cref="Done"/> never goes backwards.
/// </summary>
/// <param name="Status">Null for a plain progress tick; otherwise <c>Running</c>, <c>Succeeded</c>, <c>Failed</c> or <c>Canceled</c>.</param>
public sealed record JobStateReported(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid JobId,
    Guid? TenantId,
    string? Status,
    long? Done,
    long? Total,
    string? StageKey,
    string? ErrorKey,
    string? ErrorDetail,
    string? ResultJson) : IDcmsEvent
{
    public int Version => 1;
}
