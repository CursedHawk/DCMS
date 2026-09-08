using Dcms.PlatformApi.Delegation;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;

namespace Dcms.PlatformApi.Reporting;

/// <summary>
/// The platform's own audit log, read straight from <c>obs.v_audit_recent</c>.
///
/// <para><b>The one area of the console that is not delegated</b> (see
/// <see cref="AdminApiProxy"/> for the four that are). The reporting view already exposes a
/// superset of what this page renders, <c>dcms_platform</c> already holds SELECT on it, and the
/// view executes with its owner's rights — so reading here needs no new grant and no hop.
/// Writing is a different matter entirely and remains impossible: this role has no grant on the
/// <c>audit</c> schema at all, which is why platform-api publishes its own audit records over
/// NATS rather than appending to the chain.</para>
///
/// <para><b>It also fixes the page.</b> The console asked admin-api for
/// <c>actorDisplay</c> and <c>traceId</c>; admin-api projects <c>actor.display</c> and
/// <c>correlationId</c> and no trace id at all, so the "Who" and "Trace" columns were empty on
/// every row — an audit log that renders perfectly and tells you nothing. The view carries both
/// under those names.</para>
///
/// <para><b>Platform scope is the empty Guid</b>, not a null: <c>AuditEntry.Platform()</c>
/// writes <c>TenantId = Guid.Empty</c> and the column is not nullable. These are the records
/// that belong to the platform rather than to any workspace — tenant suspensions, role grants,
/// sign-ins — and until this console existed nothing read them at all.</para>
///
/// <para><b>The default time bound is not a nicety.</b> <c>audit.audit_events</c> is range
/// partitioned on <c>OccurredAt</c> and every index leads with <c>TenantId</c>, so a
/// platform-wide query with no lower bound scans every partition that exists. The first page
/// therefore asks for the last 30 days unless told otherwise, and paging walks backwards from
/// the cursor rather than widening the window.</para>
/// </summary>
public static class PlatformAuditEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const int DefaultWindowDays = 30;

    public sealed record AuditCursor(DateTimeOffset OccurredAt, long Seq);

    public sealed record AuditItem(
        Guid Id,
        DateTimeOffset OccurredAt,
        string Action,
        string Category,
        string Outcome,
        short Severity,
        string ActorKind,
        string? ActorDisplay,
        string ActorAttribution,
        string? ResourceType,
        string? ResourceLabel,
        string? Service,
        int? StatusCode,
        string? TraceId,
        string? CorrelationId,
        long Seq);

    public sealed record AuditPage(IReadOnlyList<AuditItem> Items, bool HasMore, AuditCursor? NextCursor);

    public static IEndpointRouteBuilder MapPlatformAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/platform/audit", async (
            ObservabilityQuery obs,
            DateTimeOffset? since,
            DateTimeOffset? beforeOccurredAt,
            long? beforeSeq,
            int? limit,
            CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var lowerBound = since ?? DateTimeOffset.UtcNow.AddDays(-DefaultWindowDays);

            // Keyset on the pair, not on the timestamp alone: records written in the same tick
            // are ordinary here -- a request and the record of its own denial share one -- and
            // ordering on occurred_at by itself would skip or repeat them across a page break.
            var keyset = beforeOccurredAt is not null && beforeSeq is not null
                ? """AND (a.occurred_at, a.seq) < (@before_at, @before_seq)"""
                : string.Empty;

            var rows = await obs.QueryAsync(
                $"""
                 SELECT a.id, a.occurred_at, a.action, a.category, a.outcome, a.severity,
                        a.actor_kind, a.actor_display, a.actor_attribution,
                        a.resource_type, a.resource_label, a.service, a.status_code,
                        a.trace_id, a.correlation_id, a.seq
                 FROM obs.v_audit_recent a
                 WHERE a.tenant_id = '00000000-0000-0000-0000-000000000000'
                   AND a.occurred_at >= @since
                   {keyset}
                 ORDER BY a.occurred_at DESC, a.seq DESC
                 LIMIT @take
                 """,
                r => new AuditItem(
                    r.GetGuid(0),
                    r.GetFieldValue<DateTimeOffset>(1),
                    r.GetString(2),
                    r.GetString(3),
                    r.GetString(4),
                    r.GetInt16(5),
                    r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    r.GetString(8),
                    r.IsDBNull(9) ? null : r.GetString(9),
                    r.IsDBNull(10) ? null : r.GetString(10),
                    r.IsDBNull(11) ? null : r.GetString(11),
                    r.IsDBNull(12) ? null : r.GetInt32(12),
                    r.IsDBNull(13) ? null : r.GetString(13),
                    r.IsDBNull(14) ? null : r.GetString(14),
                    r.GetInt64(15)),
                new Dictionary<string, object?>
                {
                    ["since"] = lowerBound,
                    ["before_at"] = beforeOccurredAt,
                    ["before_seq"] = beforeSeq,
                    // One more than asked for, so "is there another page" is answered by the
                    // query rather than by a second count over a partitioned table.
                    ["take"] = take + 1,
                },
                ct);

            var hasMore = rows.Count > take;
            var page = rows.Take(take).ToList();

            return Results.Ok(new AuditPage(
                page,
                hasMore,
                hasMore ? new AuditCursor(page[^1].OccurredAt, page[^1].Seq) : null));
        })
        .RequirePlatformPermission(PlatformConsolePermissions.AuditRead)
        .WithName("PlatformAuditRecent");

        return app;
    }
}
