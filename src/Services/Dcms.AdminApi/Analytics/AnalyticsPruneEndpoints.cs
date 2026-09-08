using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Analytics;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Analytics;

/// <summary>
/// On-demand pruning of raw analytics events, for the platform console.
///
/// <para><c>AnalyticsRetentionWorker</c> already trims past <c>Analytics:RetentionDays</c> (90)
/// on a timer. This is the same delete with a cutoff an operator chooses, for the case the
/// worker does not cover: the disk is filling now and ninety days of visitor events is the
/// biggest thing on it.</para>
///
/// <para><b><c>analytics.daily_rollups</c> is never touched.</b> The rollups are kept forever
/// on purpose — they are the only thing that still answers questions about last year once raw
/// events age out — and this endpoint has no path to them.</para>
///
/// <para>Lives in admin-api rather than the console's own service because admin-api owns the
/// analytics schema, and the console reaches it directly with the operator's own token.</para>
/// </summary>
public static class AnalyticsPruneEndpoints
{
    /// <summary>Matches the worker, so a hand-run prune behaves like a scheduled one.</summary>
    private const int BatchSize = 10_000;
    private const int MaxBatches = 200;

    /// <summary>
    /// Below this the request is refused. Deleting the last week of analytics is not retention
    /// management, it is data loss with a plausible-looking cutoff.
    /// </summary>
    private const int MinimumRetainedDays = 7;

    public sealed record PruneRequest(int OlderThanDays);

    public static IEndpointRouteBuilder MapAnalyticsPruneEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/analytics/prune", async (
            PruneRequest body,
            ConsoleCaller console,
            AnalyticsDbContext db,
            IAuditRecorder audit,
            CancellationToken ct) =>
        {
            if (!console.Allowed)
            {
                return Results.Forbid();
            }

            if (body.OlderThanDays < MinimumRetainedDays)
            {
                return Results.Problem(
                    title: "That cutoff keeps too little.",
                    detail: $"Keep at least {MinimumRetainedDays} days. Raw events are what the daily "
                          + "rollups are built from, and the rollups for a day are only correct once "
                          + "that day has been rolled up.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var cutoff = DateTimeOffset.UtcNow.AddDays(-body.OlderThanDays);

            // Recorded before the delete and allowed to fail the request, as with every purge
            // the console can start: a deletion whose record depends on the deletion succeeding
            // is missing in exactly the case worth investigating.
            await audit.RecordNowAsync(
                new AuditEntry { Action = AuditActions.AnalyticsPruned }
                    .Platform()
                    .As(AuditCategory.Security, AuditSeverity.Warning)
                    .For("analytics", "events", $"raw events older than {body.OlderThanDays} days")
                    .With("cutoff", cutoff.ToString("O"))
                    .With("retentionDays", body.OlderThanDays)
                    .With("onDemand", true),
                ct);

            var total = 0;
            for (var batch = 0; batch < MaxBatches && !ct.IsCancellationRequested; batch++)
            {
                // IgnoreQueryFilters: retention is cross-tenant by nature and this request has
                // no ambient tenant, exactly as in the worker.
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

            return Results.Ok(new
            {
                deleted = total,
                cutoff,
                // Said plainly, because "I pruned analytics and the disk did not move" is the
                // predictable next question: Postgres marks the rows dead and reuses the space,
                // it does not hand it back to the filesystem without a VACUUM FULL.
                note = "Rows are deleted; Postgres reuses the space rather than returning it to the "
                     + "filesystem. Daily rollups are untouched.",
            });
        })
        .RequireAuthorization()
        .AllowNonMemberTenant("Platform-scope retention; operates across every tenant and takes no tenant header.")
        // Exempt from the DECLARED-action mechanism, not from auditing — it records more than
        // that mechanism can. A declared entry is withdrawn when the response is 4xx or 5xx,
        // which is right for an action that did not happen and wrong for a deletion that ran
        // partway and then failed. So this writes its intent with RecordNowAsync before the
        // first batch, where a failure to record stops the delete instead of following it.
        .AuditExempt("Records AuditActions.AnalyticsPruned itself, before deleting, so the record survives a partial failure.")
        .AllowConsoleService(
            "the platform console owns this button; its API holds dcms.console and has already "
            + "checked the operator holds platform:ops:act. The delete stays here because a "
            + "DELETE grant on analytics.events is exactly what 04-platform-role.sh refuses "
            + "dcms_platform.");

        return app;
    }
}
