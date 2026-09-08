using Dcms.AdminApi.Tenancy;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Cms;

/// <summary>
/// The console's collection list: one page, filtered on the server, with a title but no drafts.
///
/// <para><b>Why a second endpoint rather than parameters on the first.</b>
/// <c>GET /api/admin/content</c> exists to hand an editor every item of another content type
/// <i>with its authored fields</i> — that is how a gig lists roster members by name — and it is
/// legitimately unpaginated, because a roster is small and the caller needs all of it. Adding a
/// default page size there would silently truncate those pickers; adding an optional one would
/// give one route two response shapes. This route has one job and one shape.</para>
///
/// <para><b>The title is computed here</b> rather than in the browser, and that is the whole
/// reason the list can stop downloading drafts. The field is the content type's first textual
/// one — the same choice the console used to make for itself — falling back to the slug.</para>
/// </summary>
public static class ContentListEndpoints
{
    public static IEndpointRouteBuilder MapContentListEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/content/page", async (
            Guid instanceId,
            string? contentType,
            string? search,
            string? status,
            string? tag,
            string? cursor,
            int? limit,
            CmsDbContext db,
            IPluginCatalog catalog,
            ITenantContext tenant,
            CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }

            var instance = await db.PluginInstances.FindAsync([instanceId], ct);
            if (instance is null)
            {
                return Results.NotFound();
            }

            // Which items carry the tag is a question the tag queries already answer, so the
            // filter is an id set rather than a second way of reading tags out of JSON.
            IReadOnlyCollection<Guid>? itemIds = null;
            if (!string.IsNullOrWhiteSpace(tag))
            {
                itemIds = await ContentListQueries.DraftItemIdsWithTagAsync(
                    db, tenantId, instanceId, contentType, tag, ct);
            }

            var page = await ContentListQueries.PageAsync(
                db,
                tenantId,
                instanceId,
                contentType,
                TitleFieldOf(catalog, instance.PluginId, contentType),
                search,
                status,
                itemIds,
                ParseCursor(cursor),
                limit ?? ContentListQueries.DefaultPageSize,
                ct);

            return Results.Ok(new
            {
                items = page.Items.Select(r => new
                {
                    id = r.Id,
                    contentType = r.ContentType,
                    slug = r.Slug,
                    title = r.Title,
                    status = r.Status,
                    updatedAt = r.UpdatedAt,
                    publishedAt = r.PublishedAt,
                    scheduledPublishAt = r.ScheduledPublishAt,
                }),
                nextCursor = page.NextCursor is { } next ? FormatCursor(next) : null,
                total = page.Total,
            });
        }).RequirePermission(PlatformPermissions.ContentRead);

        // How many items of each content type an instance holds.
        //
        // The console's collection rail showed these by fetching every item in the instance and
        // counting in the browser -- the last unbounded content read on the page, and the one
        // that ran for every instance in the rail at once whether or not it was open.
        app.MapGet("/api/admin/content/counts", async (
            Guid instanceId, CmsDbContext db, CancellationToken ct) =>
        {
            var counts = await db.ContentItems
                .Where(c => c.PluginInstanceId == instanceId)
                .GroupBy(c => c.ContentType)
                .Select(g => new { ContentType = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            return Results.Ok(counts.ToDictionary(c => c.ContentType, c => c.Count));
        }).RequirePermission(PlatformPermissions.ContentRead);

        /*
         * Everything queued to publish, across every collection in the workspace.
         *
         * The scheduler has worked since it shipped and its queue has never been visible: the
         * console could tell you that one item you were looking at was scheduled, and could not
         * answer "what goes out this week". That is the question a publishing calendar exists
         * for, and it is the only content read that deliberately crosses plugin instances.
         */
        app.MapGet("/api/admin/content/scheduled", async (
            int? limit,
            CmsDbContext db,
            IPluginCatalog catalog,
            ITenantContext tenant,
            CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }

            var rows = await ContentListQueries.ScheduledAsync(
                db, tenantId, TitleFields(catalog), limit ?? ContentListQueries.DefaultPageSize, ct);

            return Results.Ok(new
            {
                items = rows.Select(r => new
                {
                    scheduleId = r.ScheduleId,
                    itemId = r.ItemId,
                    instanceId = r.InstanceId,
                    instanceName = r.InstanceName,
                    pluginId = r.PluginId,
                    contentType = r.ContentType,
                    slug = r.Slug,
                    title = r.Title,
                    status = r.Status,
                    publishAt = r.PublishAt,
                }),
            });
        }).RequirePermission(PlatformPermissions.ContentRead);

        return app;
    }

    /// <summary>
    /// Every content type this platform can author, mapped to the field its title lives in,
    /// keyed <c>pluginId:contentType</c>.
    ///
    /// <para>Built from the catalogue rather than from the instances present, because it is
    /// small — a few dozen entries — and building it per request from the manifests is cheaper
    /// than a second query to find out which plugins this workspace happens to use.</para>
    /// </summary>
    private static Dictionary<string, string> TitleFields(IPluginCatalog catalog)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var manifest in catalog.Manifests)
        {
            foreach (var type in manifest.ContentTypes)
            {
                if (TitleFieldOf(catalog, manifest.Id, type.Name) is { } field)
                {
                    map[$"{manifest.Id}:{type.Name}"] = field;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// The field a list shows as an item's name: the first Text, RichText or Markdown field the
    /// plugin declares, then the slug field, then nothing — in which case the query falls back
    /// to the slug itself.
    ///
    /// <para>Only the plugin's own fields, deliberately. A tenant's custom fields live nested
    /// under one config key and are addressed by a dotted path; picking one of those as the
    /// title would make two tenants of the same plugin disagree about what a list column means.</para>
    /// </summary>
    private static string? TitleFieldOf(IPluginCatalog catalog, string pluginId, string? contentType)
    {
        var type = catalog.Find(pluginId)?.ContentTypes
            .FirstOrDefault(t => contentType is null || t.Name == contentType);
        if (type is null)
        {
            return null;
        }

        var textual = type.Fields.FirstOrDefault(f =>
            f.Type is ContentFieldType.Text or ContentFieldType.RichText or ContentFieldType.Markdown);

        return textual?.Name ?? type.SlugField;
    }

    /// <summary>
    /// <c>&lt;iso timestamp&gt;|&lt;guid&gt;</c>. Opaque to the client, which passes back what it
    /// was given; a malformed one starts from the beginning rather than failing, because a stale
    /// bookmark should show the first page and not an error.
    /// </summary>
    private static ContentListQueries.Cursor? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        var parts = cursor.Split('|', 2);
        return parts.Length == 2
               && DateTimeOffset.TryParse(parts[0], null,
                   System.Globalization.DateTimeStyles.RoundtripKind, out var at)
               && Guid.TryParse(parts[1], out var id)
            ? new ContentListQueries.Cursor(at, id)
            : null;
    }

    private static string FormatCursor(ContentListQueries.Cursor cursor) =>
        $"{cursor.UpdatedAt:O}|{cursor.Id}";
}
