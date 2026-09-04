using System.Security.Claims;
using Dcms.PlatformApi.Observability;
using Dcms.Shared.Audit;
using Dcms.Shared.Security;
using Microsoft.Extensions.Options;

namespace Dcms.PlatformApi.Purge;

/// <summary>
/// Deleting things that are expensive to keep and impossible to get back.
///
/// <para><b>Every purge is recorded before it runs, not after.</b> The declared-action pattern
/// used everywhere else in this codebase withdraws its record when the response is a 4xx or
/// 5xx — correct for an action that did not happen, and exactly wrong here, because a purge
/// that half-ran and then failed is the case most worth having a record of. So each of these
/// writes its intent with <c>RecordNowAsync</c>, which propagates failure: if the audit sink is
/// down, the purge does not happen at all.</para>
///
/// <para><b>Nothing here can reach <c>audit.audit_events</c>.</b> Not by configuration — by
/// construction. platform-api connects as <c>dcms_platform</c>, which holds no grant on the
/// audit schema, so there is no endpoint to write and no flag to set wrong.</para>
/// </summary>
public static class PlatformPurgeEndpoints
{
    public sealed record LokiPurgeRequest(string Selector, DateTimeOffset Start, DateTimeOffset End);
    public sealed record PrometheusPurgeRequest(IReadOnlyList<string> Matchers, DateTimeOffset? Start, DateTimeOffset? End);
    public sealed record DockerLogPurgeRequest(string Container);

    public static IEndpointRouteBuilder MapPlatformPurgeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform/purge");

        // ---- Loki ----

        group.MapGet("/loki", async (LokiClient loki, CancellationToken ct) =>
            Results.Ok(await loki.ListDeleteRequestsAsync(ct)))
            .RequirePlatformPermission(PlatformConsolePermissions.LogsRead)
            .WithName("PlatformLokiDeleteRequests");

        group.MapPost("/loki", async (
            LokiPurgeRequest body, LokiClient loki, IAuditRecorder audit,
            ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (body.End <= body.Start)
            {
                return Problem("The time range is empty.", "The end of the range must be after its start.");
            }

            // Refused, not narrowed. An operator who names an audit stream is told no — handing
            // them a silently empty result would read as "there was nothing there", which is the
            // opposite of true.
            if (LokiClient.TargetsProtectedStream(body.Selector))
            {
                await RecordRefusalAsync(audit, user, "loki", body.Selector, "protected_stream", ct);
                return Problem(
                    "Audit and security logs cannot be purged.",
                    "Those streams are kept for 90 days as an integrity control — the AUDIT ANCHOR "
                    + "lines are one of the three things that make the audit log verifiable. "
                    + "Narrow the selector to the service or container you actually meant.");
            }

            string guarded;
            try
            {
                guarded = LokiClient.Guard(body.Selector);
            }
            catch (ArgumentException ex)
            {
                return Problem("That selector cannot be used.", ex.Message);
            }

            // Before the delete, and it must succeed for the delete to proceed.
            await audit.RecordNowAsync(
                new AuditEntry { Action = AuditActions.PlatformLogsPurged }
                    .Platform()
                    .As(AuditCategory.Security, AuditSeverity.Warning)
                    .For("store", "loki")
                    .With("selector", body.Selector)
                    .With("effective_selector", guarded)
                    .With("start", body.Start)
                    .With("end", body.End),
                ct);

            await loki.CreateDeleteRequestAsync(guarded, body.Start, body.End, ct);

            return Results.Ok(new
            {
                effectiveSelector = guarded,
                // The two-hour window is the whole reason this is recoverable, so it is part of
                // the response rather than something to read in a runbook afterwards.
                cancellableUntil = DateTimeOffset.UtcNow.AddHours(2),
                message = "Queued. Loki applies deletes after a 2-hour delay, so this can still be cancelled.",
            });
        })
        .RequirePlatformPermission(PlatformConsolePermissions.LogsPurge)
        .WithName("PlatformPurgeLoki");

        group.MapDelete("/loki/{requestId}", async (
            string requestId, LokiClient loki, IAuditRecorder audit,
            ClaimsPrincipal user, CancellationToken ct) =>
        {
            await audit.RecordNowAsync(
                new AuditEntry { Action = AuditActions.PlatformLogsPurged }
                    .Platform()
                    .As(AuditCategory.Security, AuditSeverity.Info)
                    .For("store", "loki", requestId)
                    .With("cancelled_request", requestId)
                    .With("actor", user.FindFirst("sub")?.Value),
                ct);

            await loki.CancelDeleteRequestAsync(requestId, ct);
            return Results.NoContent();
        })
        .RequirePlatformPermission(PlatformConsolePermissions.LogsPurge)
        .WithName("PlatformCancelLokiPurge");

        // ---- Prometheus ----

        group.MapPost("/prometheus", async (
            PrometheusPurgeRequest body, PrometheusClient prometheus, IAuditRecorder audit,
            CancellationToken ct) =>
        {
            if (body.Matchers.Count == 0 || body.Matchers.Any(string.IsNullOrWhiteSpace))
            {
                return Problem(
                    "At least one series matcher is required.",
                    "Deleting without a matcher would drop every series in the database.");
            }

            // {__name__=~".+"} and friends match everything. Prometheus will happily do it.
            if (body.Matchers.Any(IsUnboundedMatcher))
            {
                return Problem(
                    "That matcher selects every series.",
                    "Name the metric or the label you meant. A matcher that selects everything "
                    + "deletes the platform's entire metric history, including the series the "
                    + "alerts fire on.");
            }

            await audit.RecordNowAsync(
                new AuditEntry { Action = AuditActions.PlatformLogsPurged }
                    .Platform()
                    .As(AuditCategory.Security, AuditSeverity.Warning)
                    .For("store", "prometheus")
                    .With("matchers", body.Matchers)
                    .With("start", body.Start)
                    .With("end", body.End),
                ct);

            await prometheus.DeleteSeriesAsync(body.Matchers, body.Start, body.End, ct);
            // Deletion writes tombstones; without this the series are hidden and the disk is
            // unchanged, which looks exactly like the purge not working.
            await prometheus.CleanTombstonesAsync(ct);

            return Results.Ok(new { message = "Series deleted and tombstones cleaned." });
        })
        .RequirePlatformPermission(PlatformConsolePermissions.LogsPurge)
        .WithName("PlatformPurgePrometheus");

        // ---- Docker json logs, via the janitor sidecar ----

        group.MapPost("/docker-logs", async (
            DockerLogPurgeRequest body, LogJanitorClient janitor, IOptions<ObservabilityOptions> options,
            IAuditRecorder audit, CancellationToken ct) =>
        {
            if (!options.Value.LogJanitorEnabled)
            {
                return Problem(
                    "Container log truncation is not enabled here.",
                    "It needs the log-janitor sidecar, which is off by default because it holds "
                    + "write access to Docker's container directory. Start it with the "
                    + "`logjanitor` compose profile if you want this.",
                    StatusCodes.Status501NotImplemented);
            }

            if (string.IsNullOrWhiteSpace(body.Container))
            {
                return Problem("Name a container.", "Truncating every container's log at once is not offered.");
            }

            await audit.RecordNowAsync(
                new AuditEntry { Action = AuditActions.PlatformLogsPurged }
                    .Platform()
                    .As(AuditCategory.Security, AuditSeverity.Warning)
                    .For("store", "docker", body.Container)
                    .With("container", body.Container),
                ct);

            var freed = await janitor.TruncateAsync(body.Container, ct);
            return Results.Ok(new { freedBytes = freed, message = $"Truncated the log for {body.Container}." });
        })
        .RequirePlatformPermission(PlatformConsolePermissions.LogsPurge)
        .WithName("PlatformPurgeDockerLogs");

        return app;
    }

    /// <summary>
    /// A matcher with no metric name and no equality constraint selects the whole database.
    /// Checked here because Prometheus will not check it for us.
    /// </summary>
    private static bool IsUnboundedMatcher(string matcher)
    {
        var inner = matcher.Trim().TrimStart('{').TrimEnd('}').Trim();
        if (inner.Length == 0) return true;

        // `__name__=~".+"` or `__name__=~".*"` — the canonical "everything" matchers.
        var normalised = inner.Replace(" ", string.Empty);
        return normalised is "__name__=~\".+\"" or "__name__=~\".*\"" or "__name__!=\"\"";
    }

    private static async Task RecordRefusalAsync(
        IAuditRecorder audit, ClaimsPrincipal user, string store, string selector, string reason, CancellationToken ct)
    {
        // A refused purge is recorded for the same reason a refused role revocation is: it is
        // either an operator about to be surprised, or somebody probing for what the guard
        // covers, and neither should be inferable only from an absence.
        await audit.RecordNowAsync(
            new AuditEntry { Action = AuditActions.PlatformLogsPurged }
                .Platform()
                .As(AuditCategory.Security, AuditSeverity.Warning)
                .For("store", store)
                .With("selector", selector)
                .With("actor", user.FindFirst("sub")?.Value)
                .Failed(reason),
            ct);
    }

    private static IResult Problem(string title, string detail, int status = StatusCodes.Status400BadRequest) =>
        Results.Problem(title: title, detail: detail, statusCode: status);
}
