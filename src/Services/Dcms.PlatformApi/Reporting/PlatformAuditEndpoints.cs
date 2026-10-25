using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PlatformApi.Delegation;
using Dcms.Shared.Audit.Redaction;
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

    /// <summary>Everything one record holds, for the detail a row opens. Secrets scrubbed — see <see cref="AuditPayloadScrubber"/>.</summary>
    public sealed record AuditDetail(
        Guid Id,
        Guid EventId,
        DateTimeOffset OccurredAt,
        DateTimeOffset RecordedAt,
        string Action,
        string Category,
        string Outcome,
        short Severity,
        bool IsSandbox,
        string ActorKind,
        Guid? ActorId,
        string? ActorRef,
        string? ActorDisplay,
        string ActorAttribution,
        Guid? SubjectUserId,
        string? ResourceType,
        string? ResourceId,
        string? ResourceLabel,
        string Service,
        string ServiceInstance,
        long ProducerSeq,
        string? HttpMethod,
        string? Route,
        int? StatusCode,
        string? IpAddress,
        bool IpTrusted,
        string? UserAgent,
        string? CorrelationId,
        Guid? CausationId,
        string? TraceId,
        string? SpanId,
        string? Metadata,
        string? Changes,
        short RedactionVersion,
        short SchemaVersion,
        Guid ChainKey,
        DateOnly Period,
        long Seq,
        short HashVersion,
        string? PrevHash,
        string Hash);

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

        // One record in full. The occurred_at travels with the id because the table is
        // partitioned on it: with both, the primary key finds the row in one partition; with
        // the id alone, Postgres would scan every partition there is.
        app.MapGet("/api/platform/audit/{id:guid}", async (
            ObservabilityQuery obs, Guid id, DateTimeOffset at, CancellationToken ct) =>
        {
            var rows = await obs.QueryAsync(
                """
                SELECT a.id, a.event_id, a.occurred_at, a.recorded_at, a.action, a.category,
                       a.outcome, a.severity, a.is_sandbox, a.actor_kind, a.actor_id, a.actor_ref,
                       a.actor_display, a.actor_attribution, a.subject_user_id, a.resource_type,
                       a.resource_id, a.resource_label, a.service, a.service_instance,
                       a.producer_seq, a.http_method, a.route, a.status_code, a.ip_address,
                       a.ip_trusted, a.user_agent, a.correlation_id, a.causation_id, a.trace_id,
                       a.span_id, a.metadata_json, a.changes_json, a.redaction_version,
                       a.schema_version, a.chain_key, a.period, a.seq, a.hash_version,
                       a.prev_hash, a.hash
                FROM obs.v_audit_recent a
                WHERE a.tenant_id = '00000000-0000-0000-0000-000000000000'
                  AND a.occurred_at = @at
                  AND a.id = @id
                """,
                r => new AuditDetail(
                    r.GetGuid(0),
                    r.GetGuid(1),
                    r.GetFieldValue<DateTimeOffset>(2),
                    r.GetFieldValue<DateTimeOffset>(3),
                    r.GetString(4),
                    r.GetString(5),
                    r.GetString(6),
                    r.GetInt16(7),
                    r.GetBoolean(8),
                    r.GetString(9),
                    r.IsDBNull(10) ? null : r.GetGuid(10),
                    r.IsDBNull(11) ? null : r.GetString(11),
                    r.IsDBNull(12) ? null : r.GetString(12),
                    r.GetString(13),
                    r.IsDBNull(14) ? null : r.GetGuid(14),
                    r.IsDBNull(15) ? null : r.GetString(15),
                    r.IsDBNull(16) ? null : r.GetString(16),
                    r.IsDBNull(17) ? null : r.GetString(17),
                    r.GetString(18),
                    r.GetString(19),
                    r.GetInt64(20),
                    r.IsDBNull(21) ? null : r.GetString(21),
                    r.IsDBNull(22) ? null : r.GetString(22),
                    r.IsDBNull(23) ? null : r.GetInt32(23),
                    r.IsDBNull(24) ? null : r.GetString(24),
                    r.GetBoolean(25),
                    r.IsDBNull(26) ? null : r.GetString(26),
                    r.IsDBNull(27) ? null : r.GetString(27),
                    r.IsDBNull(28) ? null : r.GetGuid(28),
                    r.IsDBNull(29) ? null : r.GetString(29),
                    r.IsDBNull(30) ? null : r.GetString(30),
                    AuditPayloadScrubber.Scrub(r.GetString(31)),
                    AuditPayloadScrubber.Scrub(r.IsDBNull(32) ? null : r.GetString(32)),
                    r.GetInt16(33),
                    r.GetInt16(34),
                    r.GetGuid(35),
                    r.GetFieldValue<DateOnly>(36),
                    r.GetInt64(37),
                    r.GetInt16(38),
                    r.IsDBNull(39) ? null : r.GetString(39),
                    r.GetString(40)),
                new Dictionary<string, object?> { ["id"] = id, ["at"] = at },
                ct);

            return rows.Count == 0 ? Results.NotFound() : Results.Ok(rows[0]);
        })
        .RequirePlatformPermission(PlatformConsolePermissions.AuditRead)
        .WithName("PlatformAuditRecord");

        return app;
    }
}

/// <summary>
/// Withholds anything that looks like a secret from a record's metadata and changes before they
/// leave platform-api.
///
/// <para>Needed because only entity diffs pass <see cref="AuditRedactor"/> on the way in;
/// <c>.With(key, value)</c> metadata and hand-written <c>.Changed(...)</c> values are recorded
/// as given. The test is the redactor's own name heuristic, so the two cannot drift: a property
/// whose name looks sensitive keeps its key and loses its value, at any depth, and a change
/// whose field looks sensitive is shown as redacted. A payload that is not JSON cannot be vetted
/// and is withheld whole.</para>
/// </summary>
public static class AuditPayloadScrubber
{
    public const string Withheld = "[withheld]";

    public static string? Scrub(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return json;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(Withheld);
        }

        Visit(node);
        return node?.ToJsonString() ?? json;
    }

    private static void Visit(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                // A field change: {"field": "ApiKey", "before": ..., "after": ..., "redacted": false}.
                if (obj["field"] is JsonValue field && field.TryGetValue<string>(out var name)
                    && AuditRedactor.LooksSensitiveName(name))
                {
                    obj["before"] = null;
                    obj["after"] = null;
                    obj["redacted"] = true;
                }
                foreach (var (key, value) in obj.ToList())
                {
                    if (value is not null && AuditRedactor.LooksSensitiveName(key))
                    {
                        obj[key] = Withheld;
                    }
                    else
                    {
                        Visit(value);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Visit(item);
                }
                break;
        }
    }
}
