using Dcms.AdminApi.Tenancy;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Shared.Kernel.Views;

namespace Dcms.AdminApi.Cms;

/// <summary>
/// The console's collection list: one page, filtered on the server, with a title but no drafts.
///
/// <para><b>Why a second endpoint rather than parameters on the first.</b>
/// <c>GET /api/admin/content</c> exists to hand an editor the items of another content type
/// <i>with their authored fields</i> — that is how a gig lists roster members by name. This route
/// has a different job (titles, filters, a count) and its own shape.</para>
///
/// <para>That route was unpaginated on the theory that "a roster is small". The 2026-09-26 load
/// test disproved it at 6,400 items: every call held every draft in memory at once, and
/// concurrent pickers crashed admin-api with <c>OutOfMemoryException</c> — for every tenant.
/// It is now paged too, with one shape (<c>{ items, nextCursor }</c>, never optional), and its
/// callers that need all of it fetch page by page, so nothing is silently truncated.</para>
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
            string? sort,
            string? dir,
            string? cursor,
            int? limit,
            string? filter,
            string? columns,
            CmsDbContext db,
            IPluginCatalog catalog,
            ITenantContext tenant,
            CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }

            // Sorted on the server because the list is paged: ordering in the browser would order
            // only the rows already loaded, and "A to Z" would restart on every page.
            ContentListQueries.Sort order;
            string? sortField = null;
            switch (sort)
            {
                case null or "" or "updated": order = ContentListQueries.Sort.Updated; break;
                case "title": order = ContentListQueries.Sort.Title; break;
                case "slug": order = ContentListQueries.Sort.Slug; break;
                // A saved view's sort by one of the collection's fields (#278).
                case { } f when f.StartsWith("field:", StringComparison.Ordinal) && f.Length > 6:
                    order = ContentListQueries.Sort.Field;
                    sortField = f[6..];
                    break;
                default: return Results.BadRequest(new { error = "sort is one of updated, title, slug, or field:{name}." });
            }
            ViewFilter? viewFilter = null;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                try
                {
                    viewFilter = JsonSerializer.Deserialize<ViewFilter>(filter, ViewJson);
                    viewFilter?.Validate();
                }
                catch (Exception e) when (e is JsonException or ViewDefinitionException)
                {
                    return Results.BadRequest(new { error = e is ViewDefinitionException ? e.Message : "filter is not a filter." });
                }
            }
            if (dir is not (null or "" or "asc" or "desc"))
            {
                return Results.BadRequest(new { error = "dir is asc or desc." });
            }
            // Newest first, and A to Z: what each order means when nobody says otherwise.
            var descending = dir is null or "" ? order == ContentListQueries.Sort.Updated : dir == "desc";

            ContentListQueries.PageCursor? after = null;
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                after = ParsePageCursor(cursor);
                if (after is null || after.Sort != order || after.Descending != descending)
                {
                    // Resuming one order from a position in another would silently skip or repeat
                    // rows; the console restarts the list when its order changes, so this is a
                    // stale or hand-made cursor.
                    return Results.BadRequest(new { error = "The cursor does not belong to this order; start the list again." });
                }
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

            var query = new ContentListQueries.ContentQuery(
                FieldsOf(catalog, instance, contentType), viewFilter, sortField,
                string.IsNullOrWhiteSpace(columns) ? null : columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            ContentListQueries.Page page;
            try
            {
                page = await ContentListQueries.PageAsync(
                db,
                tenantId,
                instanceId,
                contentType,
                TitleFieldOf(catalog, instance.PluginId, contentType),
                search,
                status,
                itemIds,
                order,
                descending,
                after,
                limit ?? ContentListQueries.DefaultPageSize,
                ct,
                query);
            }
            catch (ViewDefinitionException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }

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
                    // The view's columns, by field key; absent when none were asked for.
                    values = r.ValuesJson is null ? null : JsonNode.Parse(r.ValuesJson),
                }),
                nextCursor = page.NextCursor is { } next ? FormatPageCursor(next) : null,
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
    private static readonly JsonSerializerOptions ViewJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// What a view may filter, sort and show in a collection (#278): the type's own fields by
    /// name, and its tenant-defined ones as <c>custom.{key}</c>. Json fields are not comparable.
    /// </summary>
    internal static Dictionary<string, ContentField> FieldsOf(IPluginCatalog catalog, PluginInstance instance, string? contentType)
    {
        var fields = new Dictionary<string, ContentField>(StringComparer.Ordinal);
        var type = contentType is null ? null : catalog.Find(instance.PluginId)?.ContentTypes.FirstOrDefault(t => t.Name == contentType);
        if (type is null) return fields;
        foreach (var f in type.Fields)
        {
            ContentFieldKind? kind = f.Type switch
            {
                ContentFieldType.Text or ContentFieldType.RichText or ContentFieldType.Markdown => ContentFieldKind.Text,
                ContentFieldType.Number => ContentFieldKind.Number,
                ContentFieldType.Boolean => ContentFieldKind.Boolean,
                ContentFieldType.DateTime => ContentFieldKind.DateTime,
                ContentFieldType.Tags => ContentFieldKind.Tags,
                ContentFieldType.MediaRef or ContentFieldType.ContentRef => ContentFieldKind.Reference,
                _ => null,
            };
            if (kind is { } k) fields[f.Name] = new ContentField([f.Name], k);
        }
        if (type.CustomFields is { } custom)
        {
            try
            {
                if (JsonNode.Parse(instance.ConfigJson)?[custom.ConfigKey] is JsonArray defs)
                {
                    foreach (var def in defs.OfType<JsonObject>())
                    {
                        if (def["key"]?.GetValueKind() != JsonValueKind.String) continue;
                        var key = def["key"]!.GetValue<string>();
                        ContentFieldKind? kind = (def["type"] as JsonValue)?.ToString() switch
                        {
                            "text" or "longText" or "url" => ContentFieldKind.Text,
                            "number" => ContentFieldKind.Number,
                            "boolean" => ContentFieldKind.Boolean,
                            "date" => ContentFieldKind.DateTime,
                            "tags" => ContentFieldKind.Tags,
                            "image" => ContentFieldKind.Reference,
                            _ => null,
                        };
                        if (kind is { } k) fields[$"custom.{key}"] = new ContentField([custom.ValuesField, key], k);
                    }
                }
            }
            catch (JsonException)
            {
                // A config that is not JSON has no custom fields to filter by.
            }
        }
        return fields;
    }

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
    internal static ContentListQueries.Cursor? ParseCursor(string? cursor)
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

    internal static string FormatCursor(ContentListQueries.Cursor cursor) =>
        $"{cursor.UpdatedAt:O}|{cursor.Id}";

    /// <summary>
    /// <c>s1|{sort}|{a|d}|{id}|{key}</c>. The key goes last because it is the only part that may
    /// contain the separator — a title can hold anything.
    /// </summary>
    internal static string FormatPageCursor(ContentListQueries.PageCursor cursor) =>
        $"s1|{cursor.Sort.ToString().ToLowerInvariant()}|{(cursor.Descending ? 'd' : 'a')}|{cursor.Id}|{cursor.Key}";

    internal static ContentListQueries.PageCursor? ParsePageCursor(string cursor)
    {
        var parts = cursor.Split('|', 5);
        if (parts.Length != 5 || parts[0] != "s1" || parts[2] is not ("a" or "d")
            || !Enum.TryParse<ContentListQueries.Sort>(parts[1], ignoreCase: true, out var sort)
            || !Guid.TryParse(parts[3], out var id))
        {
            return null;
        }
        if (sort == ContentListQueries.Sort.Updated
            && !DateTimeOffset.TryParse(parts[4], null, System.Globalization.DateTimeStyles.RoundtripKind, out _))
        {
            return null;
        }
        return new ContentListQueries.PageCursor(sort, parts[2] == "d", parts[4], id);
    }
}
