using System.Linq.Expressions;
using System.Text.Json;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Jobs;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;

namespace Dcms.AdminApi.Jobs;

/// <param name="ErrorDetail">
/// What went wrong, in words. Withheld from tenants for an unexpected failure
/// (<c>jobs.error.failed</c>), whose detail is an exception message about our internals; a
/// handler's own <see cref="JobFailedException"/> detail is meant for them and is shown.
/// </param>
/// <param name="Retryable">Whether <c>POST …/retry</c> would do anything.</param>
public sealed record JobDto(
    Guid Id,
    Guid? TenantId,
    string? TenantSlug,
    string? TenantName,
    Guid? ParentId,
    string Kind,
    string Lane,
    string Source,
    string TitleKey,
    JsonElement Params,
    string Status,
    long Done,
    long? Total,
    string? StageKey,
    string? ErrorKey,
    string? ErrorDetail,
    string? LinkPath,
    bool Cancelable,
    bool CancelRequested,
    bool Retryable,
    int Attempts,
    int MaxAttempts,
    Guid? ActorUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset UpdatedAt,
    long Seq,
    JsonElement? Result,
    JobOriginDto Origin)
{
    public static JobDto From(Job j, bool forOperator, (string Slug, string? Name)? tenant = null) => new(
        j.Id, j.TenantId, tenant?.Slug, tenant?.Name, j.ParentId, j.Kind, j.Lane, j.Source, j.TitleKey,
        JsonDocument.Parse(j.ParamsJson).RootElement.Clone(),
        j.Status.ToString(), j.Done, j.Total, j.StageKey, j.ErrorKey,
        forOperator || j.ErrorKey != "jobs.error.failed" ? j.ErrorDetail : null,
        j.LinkPath,
        j.Cancelable && !j.IsFinished,
        j.CancelRequestedAt is not null && !j.IsFinished,
        j.Status is JobStatus.Failed or JobStatus.Canceled && (JobDispatch.IsDispatched(j.Lane) || j.Lane == JobLanes.Group),
        j.Attempts, j.MaxAttempts, j.ActorUserId, j.CreatedAt, j.StartedAt, j.FinishedAt, j.UpdatedAt, j.Seq,
        j.ResultJson is null ? null : JsonDocument.Parse(j.ResultJson).RootElement.Clone(),
        JobOriginDto.From(j, forOperator));
}

/// <summary>
/// Who or what started a job, read back from the request context it was enqueued under (the
/// same context its worker restores). A job queued by a worker on someone's behalf, such as a
/// Drive file's conversion, names that someone.
/// </summary>
/// <param name="ActorKind">
/// <c>User</c>, <c>EndUser</c>, <c>ServiceClient</c>, <c>Webhook</c>, <c>Plugin</c> or
/// <c>System</c>; <c>System</c> when nothing was captured.
/// </param>
/// <param name="ActorName">A person's email or name; a client, plugin or service's id otherwise.</param>
/// <param name="Service">The process that enqueued it.</param>
/// <param name="TraceId">The originating request's trace. Operators only.</param>
public sealed record JobOriginDto(string ActorKind, string? ActorName, string Service, string? TraceId)
{
    public static JobOriginDto From(Job j, bool forOperator)
    {
        var header = AuditPropagation.FromJson(j.ContextJson);
        return new(
            header(AuditPropagation.ActorKind) ?? nameof(Dcms.Shared.Kernel.Abstractions.ActorKind.System),
            header(AuditPropagation.ActorDisplay),
            j.Source,
            forOperator ? header(AuditPropagation.TraceId) : null);
    }
}

public sealed record JobPageDto(IReadOnlyList<JobDto> Items, string? NextCursor);

public sealed record JobDetailDto(JobDto Job, IReadOnlyList<JobDto> Children);

public sealed record TenantJobSummaryDto(Guid? TenantId, string? TenantSlug, string? TenantName, int Queued, int Running, int FailedLast24h);

public sealed record QueueDepthDto(string Stream, string Consumer, ulong Pending, long AckPending);

public sealed record JobOverviewDto(IReadOnlyList<TenantJobSummaryDto> Tenants, IReadOnlyList<QueueDepthDto> Queues);

/// <summary>
/// The jobs ledger over REST (ADR 0024): the admin SPA's progress ring and jobs page, and the
/// platform console's cross-tenant view (reached through platform-api).
///
/// <para><b>Who sees a job.</b> A member sees a job if they hold its
/// <see cref="Job.RequiredPermission"/> (the one gating the feature it belongs to), if it has
/// none, or if they started it. Checked at read time, unlike notifications: a job is live state
/// rather than a record of news, and somebody granted the permission mid-import should see the
/// import. Cancel and retry ask the same question. Only top-level jobs are listed; a group's
/// children come with it.</para>
/// </summary>
public static class JobEndpoints
{
    private const string VisibilityGate =
        "Gated in the handler by the job's own RequiredPermission (or authorship): the permission "
        + "differs per job, so no single endpoint permission can express it.";

    private const int DefaultLimit = 30;
    private const int MaxLimit = 100;

    /// <summary>The durable consumers whose backlog the console shows: every work queue.</summary>
    private static readonly string[] QueueStreams = ["JOBS", "MEDIA", "SITES", "EMAIL", "PLUGIN_JOBS"];

    public static void MapJobEndpoints(this WebApplication app)
    {
        const string tenantRoot = "/api/admin/jobs";

        app.MapGet(tenantRoot, async (
            JobsDbContext db, ITenantContext ctx, CurrentUser me, IPermissionResolver resolver,
            string? status, string? cursor, int? limit, CancellationToken ct) =>
        {
            var visible = await VisibleAsync(ctx, me, resolver, ct);
            var query = db.Jobs.AsNoTracking().Where(j => j.ParentId == null).Where(visible);
            return Results.Ok(await PageAsync(query, status, cursor, limit, j => JobDto.From(j, forOperator: false), ct));
        }).RequireAuthorization();

        app.MapGet(tenantRoot + "/{id:guid}", async (
            Guid id, JobsDbContext db, ITenantContext ctx, CurrentUser me, IPermissionResolver resolver, CancellationToken ct) =>
        {
            var visible = await VisibleAsync(ctx, me, resolver, ct);
            var job = await db.Jobs.AsNoTracking().Where(visible).FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null)
            {
                return Results.NotFound();
            }
            var children = await db.Jobs.AsNoTracking().Where(j => j.ParentId == id)
                .OrderBy(j => j.CreatedAt).ToListAsync(ct);
            return Results.Ok(new JobDetailDto(JobDto.From(job, false), children.Select(c => JobDto.From(c, false)).ToList()));
        }).RequireAuthorization();

        app.MapPost(tenantRoot + "/{id:guid}/cancel", async (
            Guid id, JobsDbContext db, ITenantContext ctx, CurrentUser me, IPermissionResolver resolver,
            JobControl control, CancellationToken ct) =>
        {
            var visible = await VisibleAsync(ctx, me, resolver, ct);
            var job = await db.Jobs.AsNoTracking().Where(visible).FirstOrDefaultAsync(j => j.Id == id, ct);
            return job is null ? Results.NotFound() : CancelResult(await control.CancelAsync(job, ct));
        }).RequireAuthorization()
          .WithAudit(AuditActions.JobCanceled, "job")
          .PermissionExempt(VisibilityGate);

        app.MapPost(tenantRoot + "/{id:guid}/retry", async (
            Guid id, JobsDbContext db, ITenantContext ctx, CurrentUser me, IPermissionResolver resolver,
            JobControl control, CancellationToken ct) =>
        {
            var visible = await VisibleAsync(ctx, me, resolver, ct);
            var job = await db.Jobs.AsNoTracking().Where(visible).FirstOrDefaultAsync(j => j.Id == id, ct);
            return job is null ? Results.NotFound() : RetryResult(await control.RetryAsync(job, ct));
        }).RequireAuthorization()
          .WithAudit(AuditActions.JobRetried, "job")
          .PermissionExempt(VisibilityGate);

        MapPlatform(app);
    }

    /// <summary>
    /// The console's view, every tenant at once. Called by platform-api on the console's service
    /// token with the operator propagated (or by a SuperAdmin directly); platform-api has already
    /// checked the operator's platform permission.
    /// </summary>
    private static void MapPlatform(WebApplication app)
    {
        const string root = "/api/admin/platform/jobs";

        app.MapGet(root, async (
            JobsDbContext db, TenancyDbContext tenancy, ConsoleCaller console,
            string? status, Guid? tenantId, bool? platformOnly, string? cursor, int? limit, CancellationToken ct) =>
        {
            if (!console.Allowed)
            {
                return Results.Forbid();
            }
            using var rls = RlsScope.Platform();
            var query = db.Jobs.IgnoreQueryFilters().AsNoTracking().Where(j => j.ParentId == null);
            if (tenantId is { } t)
            {
                query = query.Where(j => j.TenantId == t);
            }
            else if (platformOnly == true)
            {
                query = query.Where(j => j.TenantId == null);
            }
            var page = await PageAsync(query, status, cursor, limit, j => j, ct);
            var names = await TenantNamesAsync(tenancy, page.Items.Select(j => j.TenantId), ct);
            return Results.Ok(new JobPageDto(
                page.Items.Select(j => JobDto.From(j, forOperator: true, Name(names, j.TenantId))).ToList(),
                page.NextCursor));
        }).RequireAuthorization()
          .AllowConsoleService("The console's jobs page lists every tenant's background work.");

        app.MapGet(root + "/overview", async (
            JobsDbContext db, TenancyDbContext tenancy, ConsoleCaller console, INatsJSContext jetStream, CancellationToken ct) =>
        {
            if (!console.Allowed)
            {
                return Results.Forbid();
            }
            using var rls = RlsScope.Platform();
            var since = DateTimeOffset.UtcNow.AddHours(-24);
            var rows = await db.Jobs.IgnoreQueryFilters().AsNoTracking()
                .Where(j => j.ParentId == null
                            && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running
                                || (j.Status == JobStatus.Failed && j.UpdatedAt >= since)))
                .GroupBy(j => j.TenantId)
                .Select(g => new
                {
                    TenantId = g.Key,
                    Queued = g.Count(j => j.Status == JobStatus.Queued),
                    Running = g.Count(j => j.Status == JobStatus.Running),
                    Failed = g.Count(j => j.Status == JobStatus.Failed),
                })
                .ToListAsync(ct);
            var names = await TenantNamesAsync(tenancy, rows.Select(r => r.TenantId), ct);
            var tenants = rows
                .Select(r => new TenantJobSummaryDto(r.TenantId, Name(names, r.TenantId)?.Slug, Name(names, r.TenantId)?.Name,
                    r.Queued, r.Running, r.Failed))
                .OrderByDescending(r => r.Running).ThenByDescending(r => r.Queued)
                .ToList();
            return Results.Ok(new JobOverviewDto(tenants, await QueueDepthsAsync(jetStream, ct)));
        }).RequireAuthorization()
          .AllowConsoleService("The console's jobs page shows backlog per tenant and per queue.");

        app.MapGet(root + "/{id:guid}", async (Guid id, JobsDbContext db, TenancyDbContext tenancy, ConsoleCaller console, CancellationToken ct) =>
        {
            if (!console.Allowed)
            {
                return Results.Forbid();
            }
            using var rls = RlsScope.Platform();
            var job = await db.Jobs.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null)
            {
                return Results.NotFound();
            }
            var children = await db.Jobs.IgnoreQueryFilters().AsNoTracking()
                .Where(j => j.ParentId == id && j.TenantId == job.TenantId).OrderBy(j => j.CreatedAt).ToListAsync(ct);
            var names = await TenantNamesAsync(tenancy, [job.TenantId], ct);
            var tenantName = Name(names, job.TenantId);
            return Results.Ok(new JobDetailDto(JobDto.From(job, true, tenantName),
                children.Select(c => JobDto.From(c, true, tenantName)).ToList()));
        }).RequireAuthorization()
          .AllowConsoleService("The console's job detail.");

        app.MapPost(root + "/{id:guid}/cancel", async (Guid id, JobsDbContext db, ConsoleCaller console, JobControl control, CancellationToken ct) =>
        {
            if (!console.Allowed)
            {
                return Results.Forbid();
            }
            var job = await FindAnyAsync(db, id, ct);
            return job is null ? Results.NotFound() : CancelResult(await control.CancelAsync(job, ct));
        }).RequireAuthorization()
          .WithAudit(AuditActions.PlatformJobCanceled, "job")
          .AllowConsoleService("An operator stopping a tenant's or the platform's background job.");

        app.MapPost(root + "/{id:guid}/retry", async (Guid id, JobsDbContext db, ConsoleCaller console, JobControl control, CancellationToken ct) =>
        {
            if (!console.Allowed)
            {
                return Results.Forbid();
            }
            var job = await FindAnyAsync(db, id, ct);
            return job is null ? Results.NotFound() : RetryResult(await control.RetryAsync(job, ct));
        }).RequireAuthorization()
          .WithAudit(AuditActions.PlatformJobRetried, "job")
          .AllowConsoleService("An operator re-running a failed background job.");
    }

    /// <summary>
    /// The visibility rule as a predicate. Runs inside the request's tenant, so the EF filter
    /// already confines it to that tenant; this narrows to what the caller may see there.
    /// </summary>
    private static async Task<Expression<Func<Job, bool>>> VisibleAsync(
        ITenantContext ctx, CurrentUser me, IPermissionResolver resolver, CancellationToken ct)
    {
        var userId = me.RequireUserId();
        if (me.IsSuperAdmin)
        {
            return j => true;
        }
        var held = (await resolver.GetPermissionsAsync(ctx.TenantId!.Value, userId, ct)).ToArray();
        return j => j.RequiredPermission == null || held.Contains(j.RequiredPermission) || j.ActorUserId == userId;
    }

    /// <summary>Newest first, keyset-paged on CreatedAt (an offset skips rows as new jobs arrive).</summary>
    private static async Task<(IReadOnlyList<T> Items, string? NextCursor)> PageAsync<T>(
        IQueryable<Job> query, string? status, string? cursor, int? limit, Func<Job, T> map, CancellationToken ct)
    {
        query = status switch
        {
            "active" => query.Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
            "finished" => query.Where(j => j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed || j.Status == JobStatus.Canceled),
            "failed" => query.Where(j => j.Status == JobStatus.Failed),
            _ => query,
        };
        if (!string.IsNullOrWhiteSpace(cursor)
            && DateTimeOffset.TryParse(cursor, null, System.Globalization.DateTimeStyles.RoundtripKind, out var before))
        {
            query = query.Where(j => j.CreatedAt < before);
        }
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var rows = await query.OrderByDescending(j => j.CreatedAt).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        return (page.Select(map).ToList(), rows.Count > take ? page[^1].CreatedAt.ToString("O") : null);
    }

    private static async Task<JobPageDto> PageAsync(
        IQueryable<Job> query, string? status, string? cursor, int? limit, Func<Job, JobDto> map, CancellationToken ct)
    {
        var (items, next) = await PageAsync<JobDto>(query, status, cursor, limit, map, ct);
        return new JobPageDto(items, next);
    }

    private static async Task<Job?> FindAnyAsync(JobsDbContext db, Guid id, CancellationToken ct)
    {
        using var rls = RlsScope.Platform();
        return await db.Jobs.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
    }

    private static async Task<Dictionary<Guid, (string Slug, string? Name)>> TenantNamesAsync(
        TenancyDbContext tenancy, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Select(g => g.ToString()).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return [];
        }
        using var rls = RlsScope.Platform();
        var rows = await tenancy.Tenants.AsNoTracking().Where(t => wanted.Contains(t.Id))
            .Select(t => new { t.Id, t.Identifier, t.Name }).ToListAsync(ct);
        return rows.ToDictionary(t => Guid.Parse(t.Id), t => (t.Identifier, t.Name));
    }

    private static (string Slug, string? Name)? Name(Dictionary<Guid, (string Slug, string? Name)> names, Guid? id) =>
        id is { } g && names.TryGetValue(g, out var n) ? n : null;

    private static async Task<List<QueueDepthDto>> QueueDepthsAsync(INatsJSContext jetStream, CancellationToken ct)
    {
        var depths = new List<QueueDepthDto>();
        foreach (var stream in QueueStreams)
        {
            try
            {
                var before = depths.Count;
                await foreach (var consumer in jetStream.ListConsumersAsync(stream, ct))
                {
                    var info = consumer.Info;
                    depths.Add(new QueueDepthDto(stream, info.Name, info.NumPending, info.NumAckPending));
                }
                if (depths.Count == before)
                {
                    // Nobody drains it. Exactly the case to show: work piling up with no worker
                    // (a container that never started) looks like nothing at all otherwise.
                    var info = (await jetStream.GetStreamAsync(stream, cancellationToken: ct)).Info;
                    depths.Add(new QueueDepthDto(stream, "", (ulong)Math.Max(0, info.State.Messages), 0));
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A stream this deployment does not have (yet). The rest of the page still answers.
            }
        }
        return depths;
    }

    private static IResult CancelResult(CancelOutcome outcome) => outcome switch
    {
        CancelOutcome.Canceled => Results.Ok(new { status = "canceled" }),
        CancelOutcome.Requested => Results.Accepted(value: new { status = "cancelRequested" }),
        CancelOutcome.NotCancelable => Results.Conflict(new { error = "job-not-cancelable" }),
        _ => Results.NotFound(),
    };

    private static IResult RetryResult(bool retried) =>
        retried ? Results.Accepted(value: new { status = "queued" }) : Results.Conflict(new { error = "job-not-retryable" });
}
