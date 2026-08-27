using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Audit.Propagation;
using System.Text.Json;
using Dcms.AdminApi.Tenancy;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Cms;

/// <summary>
/// CMS authoring. Versions are immutable: every save creates a new draft version;
/// publishing pins PublishedVersionId to the latest draft and writes a
/// content.published outbox row in the same transaction.
/// </summary>
public static class ContentEndpoints
{
    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder app)
    {
        // includeDraft lets an editor pick items of another content type by their
        // authored fields (e.g. a gig's line-up listing crew members by name),
        // without a round trip per item.
        app.MapGet("/api/admin/content", async (
            Guid instanceId, string? contentType, bool? includeDraft, CmsDbContext db, CancellationToken ct) =>
        {
            var query = db.ContentItems.Where(c => c.PluginInstanceId == instanceId);
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                query = query.Where(c => c.ContentType == contentType);
            }

            var rows = await query
                .OrderByDescending(c => c.UpdatedAt)
                .Select(c => new
                {
                    id = c.Id,
                    contentType = c.ContentType,
                    slug = c.Slug,
                    status = c.Status.ToString(),
                    updatedAt = c.UpdatedAt,
                    publishedAt = c.PublishedAt,
                    draftJson = includeDraft == true
                        ? c.Versions.Where(v => v.Id == c.CurrentDraftVersionId).Select(v => v.DataJson).FirstOrDefault()
                        : null,
                })
                .ToListAsync(ct);

            // A queued publish is not a ContentStatus — the enum is Draft/Published/
            // Archived and a scheduled item is genuinely still a draft. It is reported
            // alongside the real status so the list can show (and filter on) "waiting
            // to go live" without inventing a fourth state in the database.
            var ids = rows.Select(r => r.id).ToList();
            var pending = await db.ScheduledPublishes
                .Where(sp => ids.Contains(sp.ItemId) && sp.Status == ScheduledPublishStatus.Pending)
                .GroupBy(sp => sp.ItemId)
                .Select(g => new { ItemId = g.Key, PublishAt = g.Min(sp => sp.PublishAt) })
                .ToDictionaryAsync(x => x.ItemId, x => x.PublishAt, ct);

            return Results.Ok(rows.Select(r => new
            {
                r.id,
                r.contentType,
                r.slug,
                r.status,
                r.updatedAt,
                r.publishedAt,
                scheduledPublishAt = pending.TryGetValue(r.id, out var at) ? at : (DateTimeOffset?)null,
                draft = r.draftJson is null ? (JsonElement?)null : JsonDocument.Parse(r.draftJson).RootElement,
            }));
        }).RequirePermission(PlatformPermissions.ContentRead);

        app.MapGet("/api/admin/content/{id:guid}", async (Guid id, CmsDbContext db, CancellationToken ct) =>
        {
            var item = await db.ContentItems.Include(c => c.Versions)
                .FirstOrDefaultAsync(c => c.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }
            var draft = item.Versions.FirstOrDefault(v => v.Id == item.CurrentDraftVersionId);
            // A pending schedule is invisible state that changes what happens to this
            // item without anyone touching it, so the editor has to be able to show
            // and cancel it. scheduled_publishes is not tenant-filtered (the worker
            // claims across tenants), so scope it explicitly by item.
            var scheduled = await db.ScheduledPublishes
                .Where(sp => sp.ItemId == item.Id && sp.Status == ScheduledPublishStatus.Pending)
                .OrderBy(sp => sp.PublishAt)
                .Select(sp => new { sp.Id, sp.PublishAt })
                .FirstOrDefaultAsync(ct);

            return Results.Ok(new
            {
                id = item.Id,
                contentType = item.ContentType,
                slug = item.Slug,
                status = item.Status.ToString(),
                draft = draft is null ? (JsonElement?)null : JsonDocument.Parse(draft.DataJson).RootElement,
                scheduledPublishAt = scheduled?.PublishAt,
                versions = item.Versions.OrderByDescending(v => v.VersionNo)
                    .Select(v => new { v.Id, v.VersionNo, v.CreatedAt, published = v.Id == item.PublishedVersionId }),
            });
        }).RequirePermission(PlatformPermissions.ContentRead);

        /*
         * The tag vocabulary already in use for one field of one content type, most
         * used first.
         *
         * Tags are a free-text string[] inside the version's JSON, with no shared
         * vocabulary — so nothing stops the same idea being entered as "Live", "live"
         * and "live music", which silently splits what should be one group. Feeding
         * these back as suggestions is what actually links items by tag: authors pick
         * an existing tag instead of coining a near-duplicate.
         *
         * Read from the *current draft* of each item rather than the published
         * version: a tag an author added five minutes ago should already be offered.
         */
        // The tag vocabulary an author is offered.
        //
        // Every argument is optional, and that is the feature: with none, this is
        // the *tenant's* whole vocabulary. A tag typed on an event and a tag typed
        // on a gallery item are the same tag to a reader browsing by it, so scoping
        // suggestions to one field of one content type — which is all this used to
        // do — quietly guaranteed every collection would grow its own spelling of
        // the same word.
        app.MapGet("/api/admin/content/tags", async (
            Guid? instanceId, string? contentType, string? field, CmsDbContext db,
            ITenantContext tenant, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }

            // Drafts included: an author should be offered a tag they typed five
            // minutes ago and have not published yet.
            var rows = await TagQueries.OccurrencesAsync(
                db, tenantId, publishedOnly: false,
                instanceId: instanceId,
                contentType: string.IsNullOrWhiteSpace(contentType) ? null : contentType,
                field: string.IsNullOrWhiteSpace(field) ? null : field,
                ct: ct);

            // Grouped case-insensitively, reported under the spelling used most —
            // suggesting both "Live" and "live" is not a vocabulary, and picking
            // the majority spelling renames nobody's tag.
            var tags = rows
                .GroupBy(r => r.Tag.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    tag = g.OrderByDescending(r => r.Count).First().Tag.Trim(),
                    count = g.Sum(r => r.Count),
                    occurrences = g
                        .Select(r => new { r.InstanceSlug, r.ContentType, r.Field, r.Count })
                        .OrderByDescending(r => r.Count)
                        .ToList(),
                })
                .OrderByDescending(t => t.count)
                .ThenBy(t => t.tag, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Ok(tags);
        }).RequirePermission(PlatformPermissions.ContentRead);

        app.MapPost("/api/admin/content", async (
            CreateContentRequest body, IPluginCatalog catalog, CmsDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var instance = await db.PluginInstances.FirstOrDefaultAsync(p => p.Id == body.PluginInstanceId, ct);
            if (instance is null)
            {
                return Results.BadRequest(new { error = "Unknown plugin instance." });
            }
            if (!PluginDeclaresType(catalog, instance.PluginId, body.ContentType))
            {
                return Results.BadRequest(new { error = $"Plugin does not define content type '{body.ContentType}'." });
            }
            if (await db.ContentItems.AnyAsync(c =>
                c.PluginInstanceId == instance.Id && c.ContentType == body.ContentType && c.Slug == body.Slug, ct))
            {
                return Results.Conflict(new { error = "Slug already exists for this content type." });
            }

            var item = new ContentItem
            {
                Id = Guid.NewGuid(),
                PluginInstanceId = instance.Id,
                ContentType = body.ContentType,
                Slug = body.Slug,
                Status = ContentStatus.Draft,
                CreatedBy = me.UserId,
            };
            var version = NewVersion(item, 1, body.Data, me.UserId);
            item.CurrentDraftVersionId = version.Id;
            item.Versions.Add(version);
            db.ContentItems.Add(item);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/content/{item.Id}", new { id = item.Id });
        }).RequirePermission(PlatformPermissions.ContentWrite).WithAudit(AuditActions.ContentCreated, "content_item");

        app.MapPut("/api/admin/content/{id:guid}", async (
            Guid id, UpdateContentRequest body, CmsDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var item = await db.ContentItems.Include(c => c.Versions).FirstOrDefaultAsync(c => c.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }
            var nextNo = item.Versions.Count == 0 ? 1 : item.Versions.Max(v => v.VersionNo) + 1;
            var version = NewVersion(item, nextNo, body.Data, me.UserId);
            // Added through the set, not through item.Versions: the key is assigned
            // here rather than by the store, so a new version reached via a tracked
            // entity's navigation is attached as an existing row and saved as an
            // UPDATE that matches nothing. (The create path gets away with the
            // navigation because db.ContentItems.Add cascades Added to the graph.)
            db.ContentVersions.Add(version);
            item.CurrentDraftVersionId = version.Id;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { versionId = version.Id, versionNo = version.VersionNo });
        }).RequirePermission(PlatformPermissions.ContentWrite).WithAudit(AuditActions.ContentUpdated, "content_item");

        // An optional `data` body makes this "save and publish": the edits become a new
        // draft version and that version is published, in one transaction.
        //
        // Without it, publishing pins whatever was last *saved*. An author who types,
        // then clicks Publish, would silently ship the previous revision — the same
        // trap applies to scheduling, which is why it takes `data` too.
        app.MapPost("/api/admin/content/{id:guid}/publish", async (
            Guid id, PublishRequest? body, CmsDbContext db, ITenantContext tenant,
            CurrentUser me, AuditScope scope, DcmsMetrics metrics, CancellationToken ct) =>
        {
            var item = await db.ContentItems.Include(c => c.Versions)
                .FirstOrDefaultAsync(c => c.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }
            if (body?.Data is { } edits)
            {
                AddDraftVersion(db, item, edits, me.UserId);
            }
            if (item.CurrentDraftVersionId is null)
            {
                return Results.BadRequest(new { error = "Nothing to publish." });
            }
            var instance = await db.PluginInstances.FirstOrDefaultAsync(p => p.Id == item.PluginInstanceId, ct);

            item.PublishedVersionId = item.CurrentDraftVersionId;
            item.Status = ContentStatus.Published;
            item.PublishedAt = DateTimeOffset.UtcNow;
            item.UpdatedAt = item.PublishedAt.Value;

            var evt = new ContentPublished(Guid.NewGuid(), DateTimeOffset.UtcNow,
                tenant.TenantId!.Value, item.PluginInstanceId, item.Id, item.ContentType, item.Slug);
            db.Outbox.Add(new ContentOutboxMessage
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                Subject = Subjects.ContentPublished,
                PayloadJson = JsonSerializer.Serialize(evt),
                // Carries the publisher across the dispatcher's two-second gap, so the site
                // build this sets off still names the person who clicked publish.
                ContextJson = AuditPropagation.CaptureJson(scope),
            });
            await db.SaveChangesAsync(ct);

            // After the commit, not before: the outbox row and the state change land in one
            // transaction, and a counter incremented for a publish that then rolled back is a
            // number no dashboard can reconcile against the content it claims to describe.
            metrics.ContentPublished(tenant.TenantId!.Value, item.ContentType);
            return Results.Ok(new { status = "published", instanceSlug = instance?.Slug });
        }).RequirePermission(PlatformPermissions.ContentPublish).WithAudit(AuditActions.ContentPublished, "content_item");

        app.MapPost("/api/admin/content/{id:guid}/schedule", async (
            Guid id, ScheduleRequest body, CmsDbContext db, ITenantContext tenant,
            CurrentUser me, IAuditRecorder audit, AuditScope scope, CancellationToken ct) =>
        {
            var item = await db.ContentItems.Include(c => c.Versions)
                .FirstOrDefaultAsync(c => c.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }
            if (body.PublishAt <= DateTimeOffset.UtcNow)
            {
                return Results.BadRequest(new { error = "publishAt must be in the future." });
            }
            if (body.Data is { } edits)
            {
                AddDraftVersion(db, item, edits, me.UserId);
            }
            if (item.CurrentDraftVersionId is null)
            {
                return Results.BadRequest(new { error = "Nothing to schedule." });
            }

            // One pending schedule per item. Re-scheduling used to stack a second row,
            // so the item published twice — at the old time with the old version, then
            // again at the new one.
            // Clearing the old row is part of scheduling, not an act of its own; the record
            // below says what the item is scheduled for now, which is the whole story.
            using var _ = scope.SuppressBulkCapture();
            await db.ScheduledPublishes
                .Where(sp => sp.ItemId == item.Id && sp.Status == ScheduledPublishStatus.Pending)
                .ExecuteDeleteAsync(ct);

            var scheduled = new ScheduledPublish
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                ItemId = item.Id,
                VersionId = item.CurrentDraftVersionId.Value,
                PublishAt = body.PublishAt,
            };
            db.ScheduledPublishes.Add(scheduled);

            audit.Declared?
                .For("content_item", item.Id, item.Slug)
                .With("publish_at", body.PublishAt)
                .With("version_id", scheduled.VersionId);

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { scheduleId = scheduled.Id, publishAt = scheduled.PublishAt });
        }).RequirePermission(PlatformPermissions.ContentPublish).WithAudit(AuditActions.ContentScheduled, "content_item");

        // Cancel a pending schedule. Scoped by item id rather than schedule id so the
        // editor can cancel from what it displays without holding the row's key.
        app.MapDelete("/api/admin/content/{id:guid}/schedule", async (
            Guid id, CmsDbContext db, IAuditRecorder audit, AuditScope scope, CancellationToken ct) =>
        {
            if (!await db.ContentItems.AnyAsync(c => c.Id == id, ct))
            {
                return Results.NotFound();
            }

            using var _ = scope.SuppressBulkCapture();
            var cancelled = await db.ScheduledPublishes
                .Where(sp => sp.ItemId == id && sp.Status == ScheduledPublishStatus.Pending)
                .ExecuteDeleteAsync(ct);

            audit.Declared?.With("cancelled", cancelled);

            return Results.Ok(new { cancelled });
        }).RequirePermission(PlatformPermissions.ContentPublish).WithAudit(AuditActions.ContentScheduleCancelled, "content_item");

        app.MapPost("/api/admin/content/{id:guid}/unpublish", async (
            Guid id, CmsDbContext db, ITenantContext tenant, AuditScope scope,
            DcmsMetrics metrics, CancellationToken ct) =>
        {
            var item = await db.ContentItems.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }
            item.PublishedVersionId = null;
            item.Status = ContentStatus.Draft;
            item.PublishedAt = null;
            item.UpdatedAt = DateTimeOffset.UtcNow;

            var evt = new ContentUnpublished(Guid.NewGuid(), DateTimeOffset.UtcNow,
                tenant.TenantId!.Value, item.PluginInstanceId, item.Id, item.ContentType, item.Slug);
            db.Outbox.Add(new ContentOutboxMessage
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                Subject = Subjects.ContentUnpublished,
                PayloadJson = JsonSerializer.Serialize(evt),
                ContextJson = AuditPropagation.CaptureJson(scope),
            });
            await db.SaveChangesAsync(ct);
            metrics.ContentUnpublished(tenant.TenantId!.Value, item.ContentType);
            return Results.Ok(new { status = "unpublished" });
        }).RequirePermission(PlatformPermissions.ContentPublish).WithAudit(AuditActions.ContentUnpublished, "content_item");

        return app;
    }

    private static ContentVersion NewVersion(ContentItem item, int no, JsonElement? data, Guid? user) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = item.TenantId,
        ItemId = item.Id,
        VersionNo = no,
        DataJson = data?.GetRawText() ?? "{}",
        CreatedBy = user,
    };

    /// <summary>
    /// Appends a new draft version and points the item at it, exactly as the PUT
    /// path does. Shared by publish and schedule so those can carry the edits being
    /// acted on. The caller saves; this only stages.
    ///
    /// The version is added through the DbSet, not through <c>item.Versions</c>: the
    /// key is assigned here rather than by the store, so a new version reached via a
    /// tracked entity's navigation is attached as an existing row and saved as an
    /// UPDATE that matches nothing.
    /// </summary>
    private static void AddDraftVersion(CmsDbContext db, ContentItem item, JsonElement data, Guid? user)
    {
        var nextNo = item.Versions.Count == 0 ? 1 : item.Versions.Max(v => v.VersionNo) + 1;
        var version = NewVersion(item, nextNo, data, user);
        db.ContentVersions.Add(version);
        item.Versions.Add(version);
        item.CurrentDraftVersionId = version.Id;
        item.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static bool PluginDeclaresType(IPluginCatalog catalog, string pluginId, string contentType)
        => catalog.Find(pluginId)?.ContentTypes.Any(t => t.Name == contentType) ?? false;

    private sealed record CreateContentRequest(Guid PluginInstanceId, string ContentType, string Slug, JsonElement? Data);
    private sealed record UpdateContentRequest(JsonElement? Data);
    private sealed record PublishRequest(JsonElement? Data);
    private sealed record ScheduleRequest(DateTimeOffset PublishAt, JsonElement? Data);
}
