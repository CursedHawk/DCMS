using System.Text.Json;
using Dcms.AdminApi.Tenancy;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
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

            return Results.Ok(rows.Select(r => new
            {
                r.id,
                r.contentType,
                r.slug,
                r.status,
                r.updatedAt,
                r.publishedAt,
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
            return Results.Ok(new
            {
                id = item.Id,
                contentType = item.ContentType,
                slug = item.Slug,
                status = item.Status.ToString(),
                draft = draft is null ? (JsonElement?)null : JsonDocument.Parse(draft.DataJson).RootElement,
                versions = item.Versions.OrderByDescending(v => v.VersionNo)
                    .Select(v => new { v.Id, v.VersionNo, v.CreatedAt, published = v.Id == item.PublishedVersionId }),
            });
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
        }).RequirePermission(PlatformPermissions.ContentWrite);

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
        }).RequirePermission(PlatformPermissions.ContentWrite);

        app.MapPost("/api/admin/content/{id:guid}/publish", async (
            Guid id, CmsDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var item = await db.ContentItems.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
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
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { status = "published", instanceSlug = instance?.Slug });
        }).RequirePermission(PlatformPermissions.ContentPublish);

        app.MapPost("/api/admin/content/{id:guid}/schedule", async (
            Guid id, ScheduleRequest body, CmsDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var item = await db.ContentItems.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }
            if (item.CurrentDraftVersionId is null)
            {
                return Results.BadRequest(new { error = "Nothing to schedule." });
            }
            if (body.PublishAt <= DateTimeOffset.UtcNow)
            {
                return Results.BadRequest(new { error = "publishAt must be in the future." });
            }

            var scheduled = new ScheduledPublish
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                ItemId = item.Id,
                VersionId = item.CurrentDraftVersionId.Value,
                PublishAt = body.PublishAt,
            };
            db.ScheduledPublishes.Add(scheduled);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { scheduleId = scheduled.Id, publishAt = scheduled.PublishAt });
        }).RequirePermission(PlatformPermissions.ContentPublish);

        app.MapPost("/api/admin/content/{id:guid}/unpublish", async (
            Guid id, CmsDbContext db, ITenantContext tenant, CancellationToken ct) =>
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
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { status = "unpublished" });
        }).RequirePermission(PlatformPermissions.ContentPublish);

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

    private static bool PluginDeclaresType(IPluginCatalog catalog, string pluginId, string contentType)
        => catalog.Find(pluginId)?.ContentTypes.Any(t => t.Name == contentType) ?? false;

    private sealed record CreateContentRequest(Guid PluginInstanceId, string ContentType, string Slug, JsonElement? Data);
    private sealed record UpdateContentRequest(JsonElement? Data);
    private sealed record ScheduleRequest(DateTimeOffset PublishAt);
}
