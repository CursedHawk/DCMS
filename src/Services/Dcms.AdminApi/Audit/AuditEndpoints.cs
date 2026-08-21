using System.Text.Json;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Audit;

/// <summary>
/// Reading the audit log. Deliberately read-only: there is no endpoint that edits or deletes a
/// record, and there never should be.
/// </summary>
public static class AuditEndpoints
{
    private const int MaxPageSize = 200;

    /// <summary>
    /// Ceiling on one export. Not a security control — someone with audit:export may page for
    /// as long as they like — but a bound that keeps a single request from holding a connection
    /// while it materialises a year of a busy tenant's history.
    /// </summary>
    private const int MaxExportRows = 50_000;

    /// <summary>Always LF, never the platform's newline: NDJSON's record separator is defined.</summary>
    private static readonly byte[] Newline = "\n"u8.ToArray();

    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/audit", async (
            AuditDbContext db,
            TenancyDbContext tenancy,
            ITenantContext tenantContext,
            string? action,
            string? category,
            string? outcome,
            string? resourceType,
            string? resourceId,
            Guid? actorId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            DateTimeOffset? beforeOccurredAt,
            long? beforeSeq,
            int? limit,
            CancellationToken ct) =>
        {
            var tenantId = tenantContext.TenantId;
            if (tenantId is null)
            {
                return Results.BadRequest(new { error = "No tenant selected." });
            }

            var take = Math.Clamp(limit ?? 50, 1, MaxPageSize);

            var query = ApplyFilters(
                await VisibleAsync(db, tenancy, tenantId.Value, ct),
                new AuditFilter(action, category, outcome, resourceType, resourceId, actorId, from, to));

            // Keyset, not offset: the log only grows, and an offset page walks further into the
            // table on every request while new rows shift the window under the reader.
            if (beforeOccurredAt is not null)
            {
                var cursorSeq = beforeSeq ?? long.MaxValue;
                query = query.Where(e =>
                    e.OccurredAt < beforeOccurredAt
                    || (e.OccurredAt == beforeOccurredAt && e.Seq < cursorSeq));
            }

            var rows = await query
                .OrderByDescending(e => e.OccurredAt)
                .ThenByDescending(e => e.Seq)
                .Take(take + 1)
                .ToListAsync(ct);

            var hasMore = rows.Count > take;
            if (hasMore)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            var viewingTenantId = tenantId.Value;
            var items = rows.Select(e => Project(e, viewingTenantId)).ToList();
            var last = rows.Count > 0 ? rows[^1] : null;

            return Results.Ok(new
            {
                items,
                hasMore,
                nextCursor = last is null ? null : new { occurredAt = last.OccurredAt, seq = last.Seq },
            });
        }).RequirePermission(PlatformPermissions.AuditRead);

        app.MapGet("/api/admin/audit/verify", async (
            AuditChainVerifier verifier,
            ITenantContext tenantContext,
            CancellationToken ct) =>
        {
            var tenantId = tenantContext.TenantId;
            if (tenantId is null)
            {
                return Results.BadRequest(new { error = "No tenant selected." });
            }

            var segments = await verifier.VerifyTenantAsync(tenantId.Value, ct);
            return Results.Ok(new
            {
                valid = segments.All(s => s.Valid),
                segments = segments.Select(s => new
                {
                    period = s.Period,
                    s.Checked,
                    s.Valid,
                    firstBadSeq = s.FirstBadSeq,
                    s.Reason,
                }),
            });
        }).RequirePermission(PlatformPermissions.AuditRead);

        // One record in full. The list is deliberately terse — two hundred rows of diffs is
        // unreadable — so this is where the field changes and the metadata are actually looked
        // at. Same visibility rules: a record the caller's tenant may not see is a 404, not a
        // 403, because "that id exists but is not yours" is itself a disclosure.
        app.MapGet("/api/admin/audit/{id:guid}", async (
            Guid id,
            AuditDbContext db,
            TenancyDbContext tenancy,
            ITenantContext tenantContext,
            CancellationToken ct) =>
        {
            if (tenantContext.TenantId is not { } tenantId)
            {
                return Results.BadRequest(new { error = "No tenant selected." });
            }

            var visible = await VisibleAsync(db, tenancy, tenantId, ct);
            var row = await visible
                .FirstOrDefaultAsync(e => e.Id == id, ct);

            return row is null ? Results.NotFound() : Results.Ok(Project(row, tenantId));
        }).RequirePermission(PlatformPermissions.AuditRead);

        // Export, as newline-delimited JSON.
        //
        // NDJSON rather than a JSON array because the result is unbounded and streamed: a
        // reader can start processing the first record before the last one is written, and a
        // truncated download is still a valid prefix rather than a broken document. CSV would
        // have to flatten the diff and the metadata, which is most of what an export is for.
        app.MapGet("/api/admin/audit/export", async (
            HttpContext http,
            AuditDbContext db,
            TenancyDbContext tenancy,
            ITenantContext tenantContext,
            IAuditRecorder audit,
            string? action,
            string? category,
            string? outcome,
            string? resourceType,
            string? resourceId,
            Guid? actorId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            CancellationToken ct) =>
        {
            if (tenantContext.TenantId is not { } tenantId)
            {
                return Results.BadRequest(new { error = "No tenant selected." });
            }

            var take = Math.Clamp(limit ?? MaxExportRows, 1, MaxExportRows);
            var filter = new AuditFilter(action, category, outcome, resourceType, resourceId, actorId, from, to);

            // Recorded before a byte is written. Taking a copy of the audit log out of the
            // platform is itself one of the more sensitive things anyone can do here, and it is
            // the one action whose record cannot be left to the generic fallback: the response
            // is a stream, so by the time it ends the request has already been answered.
            audit.Record(AuditActions.AuditExported)
                .As(AuditCategory.Access, AuditSeverity.Notice)
                .With("filter", filter.Describe())
                .With("limit", take);
            await audit.FlushAsync(ct);

            var rows = await ApplyFilters(await VisibleAsync(db, tenancy, tenantId, ct), filter)
                .OrderByDescending(e => e.OccurredAt)
                .ThenByDescending(e => e.Seq)
                .Take(take)
                .ToListAsync(ct);

            http.Response.ContentType = "application/x-ndjson";
            http.Response.Headers.ContentDisposition =
                $"attachment; filename=\"audit-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.ndjson\"";

            foreach (var row in rows)
            {
                await JsonSerializer.SerializeAsync(http.Response.Body, Project(row, tenantId), ExportJson, ct);
                await http.Response.Body.WriteAsync(Newline, ct);
            }

            return Results.Empty;
        }).RequirePermission(PlatformPermissions.AuditExport)
          .WithAudit(AuditActions.AuditExported, category: AuditCategory.Access);

        return app;
    }

    /// <summary>
    /// The rows this tenant is allowed to see: its own, plus the platform-scope records about
    /// its own members.
    ///
    /// <para>Platform-scope records (logins, account changes) carry <see cref="Guid.Empty"/> and
    /// belong to no tenant. A tenant may still see the ones about its own members — that is
    /// usually the whole reason someone opens this page — so they are resolved by membership
    /// rather than duplicated per tenant at write time, which would break dedup and freeze a
    /// membership snapshot into the log.</para>
    /// </summary>
    private static async Task<IQueryable<AuditEventRow>> VisibleAsync(
        AuditDbContext db, TenancyDbContext tenancy, Guid tenantId, CancellationToken ct)
    {
        // Materialised, and it has to be. The membership rows belong to TenancyDbContext and
        // the audit rows to AuditDbContext; composing one context's IQueryable into another's
        // throws "Cannot use multiple context instances within a single query execution" at
        // the moment the query runs — which made every read of this log a 500 while the writes
        // behind it kept working perfectly. Same database, but that is not the same thing as
        // the same context.
        //
        // Safe to pull into memory: this is one tenant's membership list, which is bounded by
        // how many people work there.
        var memberIds = await tenancy.Memberships
            .Where(m => m.TenantId == tenantId)
            .Select(m => m.UserId)
            .ToListAsync(ct);

        return db.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                || (e.TenantId == Guid.Empty && e.SubjectUserId != null && memberIds.Contains(e.SubjectUserId.Value)));
    }

    /// <summary>
    /// What the list and the export both narrow by. Shared so the two cannot drift: an export
    /// that quietly ignored a filter the page had applied would hand someone more than they
    /// asked for and more than they think they got.
    /// </summary>
    private sealed record AuditFilter(
        string? Action,
        string? Category,
        string? Outcome,
        string? ResourceType,
        string? ResourceId,
        Guid? ActorId,
        DateTimeOffset? From,
        DateTimeOffset? To)
    {
        /// <summary>The filter as recorded on the export's own audit entry.</summary>
        public Dictionary<string, object?> Describe()
        {
            var described = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(Action)) described["action"] = Action;
            if (!string.IsNullOrWhiteSpace(Category)) described["category"] = Category;
            if (!string.IsNullOrWhiteSpace(Outcome)) described["outcome"] = Outcome;
            if (!string.IsNullOrWhiteSpace(ResourceType)) described["resourceType"] = ResourceType;
            if (!string.IsNullOrWhiteSpace(ResourceId)) described["resourceId"] = ResourceId;
            if (ActorId is not null) described["actorId"] = ActorId;
            if (From is not null) described["from"] = From;
            if (To is not null) described["to"] = To;
            // An unfiltered export is the interesting case; say so rather than leaving it blank.
            return described.Count == 0
                ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["scope"] = "everything" }
                : described;
        }
    }

    private static IQueryable<AuditEventRow> ApplyFilters(IQueryable<AuditEventRow> query, AuditFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            // Prefix match, so "site." selects the whole family without listing each key.
            query = query.Where(e => e.Action.StartsWith(filter.Action));
        }
        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            var category = filter.Category.ToLowerInvariant();
            query = query.Where(e => e.Category == category);
        }
        if (!string.IsNullOrWhiteSpace(filter.Outcome))
        {
            var outcome = filter.Outcome.ToLowerInvariant();
            query = query.Where(e => e.Outcome == outcome);
        }
        if (!string.IsNullOrWhiteSpace(filter.ResourceType))
        {
            query = query.Where(e => e.ResourceType == filter.ResourceType);
            if (!string.IsNullOrWhiteSpace(filter.ResourceId))
            {
                query = query.Where(e => e.ResourceId == filter.ResourceId);
            }
        }
        if (filter.ActorId is not null)
        {
            query = query.Where(e => e.ActorId == filter.ActorId);
        }
        if (filter.From is not null)
        {
            query = query.Where(e => e.OccurredAt >= filter.From);
        }
        if (filter.To is not null)
        {
            query = query.Where(e => e.OccurredAt < filter.To);
        }
        return query;
    }

    /// <summary>
    /// Shapes a row for the client.
    ///
    /// <para>Platform-scope records get a <b>restricted projection</b>. A login belongs to the
    /// user, not to any one tenant, and the same person may be a member of several: handing
    /// tenant A the full metadata of a sign-in would leak where else that account is used. The
    /// restriction is a whitelist — new fields are hidden by default rather than exposed until
    /// someone remembers to hide them.</para>
    /// </summary>
    private static object Project(AuditEventRow e, Guid viewingTenantId)
    {
        var isPlatformScope = e.TenantId != viewingTenantId;

        return new
        {
            id = e.Id,
            occurredAt = e.OccurredAt,
            seq = e.Seq,
            action = e.Action,
            category = e.Category,
            outcome = e.Outcome,
            severity = e.Severity,
            actor = new
            {
                kind = e.ActorKind,
                id = e.ActorId,
                @ref = e.ActorRef,
                display = e.ActorDisplay,
                attribution = e.ActorAttribution,
            },
            subjectUserId = e.SubjectUserId,
            resource = isPlatformScope ? null : new
            {
                type = e.ResourceType,
                id = e.ResourceId,
                label = e.ResourceLabel,
            },
            service = e.ServiceName,
            correlationId = e.CorrelationId,
            http = isPlatformScope ? null : new
            {
                method = e.HttpMethod,
                route = e.RoutePattern,
                status = e.StatusCode,
            },
            ipAddress = e.IpAddress,
            // Surfaced so the UI can label the address as reported rather than verified: every
            // service that records one currently trusts any forwarding proxy.
            ipTrusted = e.IpTrusted,
            isSandbox = e.IsSandbox,
            platformScope = isPlatformScope,
            metadata = isPlatformScope ? null : e.MetadataJson,
            changes = isPlatformScope ? null : e.ChangesJson,
            // Which redaction policy produced the diff. Without it, a reader years from now
            // cannot tell a field that was blank from one that this version withheld.
            redactionVersion = e.RedactionVersion,
        };
    }
}
