using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Cms;

/// <summary>
/// The tenant's tag vocabulary, read straight from the content JSON.
///
/// Tags are not a table. A plugin declares a `Tags` field and the values land in
/// the version's `DataJson` as a JSON array, which is the right storage — a tag
/// belongs to the item, and a separate table would need a migration every time a
/// plugin added a field. The cost is that "which tags does this tenant use" is a
/// question no navigation property can answer, so it is asked here: unnest every
/// array-valued field of every item and group.
///
/// Raw ADO rather than EF, because that is `jsonb_array_elements_text` over a
/// lateral join, which LINQ cannot express and `Database.SqlQuery` cannot project
/// (it only reads scalars). Every input is a bound parameter — nothing is
/// interpolated into the command text.
///
/// Two audiences, one query shape, distinguished by <c>publishedOnly</c>:
/// the admin suggests from the *draft* vocabulary (an author should be offered a
/// tag they typed five minutes ago and have not published), while delivery serves
/// only what a visitor can actually reach.
/// </summary>
public static class TagQueries
{
    /// <summary>Enough to fill a picker or a tag index; not an export.</summary>
    public const int MaxTags = 500;

    /// <summary>
    /// Every array-valued field of a version's data, one level deep.
    ///
    /// The second branch is what finds a tenant's *own* fields. A plugin that
    /// supports custom fields nests them all under one key, so `values.barva` is
    /// an array inside an object and a top-level scan walks straight past it —
    /// which meant a tenant's own tag fields, the ones that make a site specific,
    /// contributed nothing to the vocabulary and got no suggestions. Their path is
    /// reported dotted (`values.barva`), the same way the renderers address them.
    /// </summary>
    private const string ArrayFields = """
        CROSS JOIN LATERAL (
            SELECT e.key AS field, e.value AS arr
            FROM jsonb_each(v."DataJson") e
            WHERE jsonb_typeof(e.value) = 'array'
            UNION ALL
            SELECT e.key || '.' || n.key AS field, n.value AS arr
            FROM jsonb_each(v."DataJson") e
            CROSS JOIN LATERAL jsonb_each(e.value) n
            WHERE jsonb_typeof(e.value) = 'object' AND jsonb_typeof(n.value) = 'array'
        ) entry
        CROSS JOIN LATERAL jsonb_array_elements_text(entry.arr) AS tag
        """;

    /// <summary>
    /// What an array of strings has to look like to be a vocabulary.
    ///
    /// Nothing in the data says "this field holds tags" — a `MediaRef[]` is an
    /// array of strings exactly like a `Tags[]` is, so the first version of this
    /// indexed every photo on the site as a tag and buried the real vocabulary
    /// under a wall of asset ids. These are the shapes a tag provably never has:
    /// a GUID (an asset or content reference), a URL or a path, and anything long
    /// enough to be prose. Deliberately a rule about the *value* rather than the
    /// field name — a guess at "photos"/"images" would misfire on the tenant that
    /// names a tag field something unexpected, in both directions.
    /// </summary>
    private const string LooksLikeATag = """
        AND length(trim(tag.value)) BETWEEN 1 AND 64
        AND tag.value !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
        AND tag.value !~ '^(/|https?://)'
        """;

    /// <summary>
    /// Matches `@field` against either the whole path or its last segment, so an
    /// author who typed the field name they see in the plugin's own admin screen
    /// (`barva`) reaches `values.barva`.
    ///
    /// Appended only when a field was asked for. An optional filter written as
    /// `@p IS NULL OR …` would make Postgres resolve the type of a parameter whose
    /// only value is NULL, which it cannot do — the clause has to be absent, not
    /// merely satisfied.
    /// </summary>
    private const string FieldMatches =
        "AND (entry.field = @field OR split_part(entry.field, '.', 2) = @field)";

    /// <summary>One tag, and one place it is used.</summary>
    public sealed record TagOccurrence(
        string Tag, string InstanceSlug, string ContentType, string Field, int Count);

    /// <summary>
    /// Every tag in use, with where it occurs.
    ///
    /// `field` names the content field the tag came from, because a content type
    /// can declare several tag-ish fields (an event has genres *and* tags) and
    /// collapsing them would offer a genre as a tag.
    /// </summary>
    public static async Task<List<TagOccurrence>> OccurrencesAsync(
        DbContext db,
        Guid tenantId,
        bool publishedOnly,
        Guid? instanceId = null,
        string? contentType = null,
        string? field = null,
        CancellationToken ct = default)
    {
        // The version each row reads: the published one for delivery, the working
        // draft for the admin (falling back to the published one, or an item that
        // was published and never edited since would contribute nothing).
        var versionJoin = publishedOnly
            ? """JOIN cms.content_versions v ON v."Id" = i."PublishedVersionId" """
            : """JOIN cms.content_versions v ON v."Id" = COALESCE(i."CurrentDraftVersionId", i."PublishedVersionId") """;

        // Each optional filter is a clause that is present or absent, never a
        // parameter compared against NULL — see FieldMatches.
        var parameters = new List<(string, object?)> { ("@tenantId", tenantId) };
        var filters = new List<string>();
        if (publishedOnly)
        {
            filters.Add($"AND i.\"Status\" = '{nameof(ContentStatus.Published)}'");
        }
        if (instanceId is { } instance)
        {
            filters.Add("AND i.\"PluginInstanceId\" = @instanceId");
            parameters.Add(("@instanceId", instance));
        }
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            filters.Add("AND i.\"ContentType\" = @contentType");
            parameters.Add(("@contentType", contentType));
        }
        if (!string.IsNullOrWhiteSpace(field))
        {
            filters.Add(FieldMatches);
            parameters.Add(("@field", field));
        }

        var sql = $"""
            SELECT tag.value AS tag, p."Slug" AS instance, i."ContentType" AS type,
                   entry.field AS field, count(*)::int AS uses
            FROM cms.content_items i
            {versionJoin}
            JOIN plugins.plugin_instances p ON p."Id" = i."PluginInstanceId"
            {ArrayFields}
            WHERE i."TenantId" = @tenantId
              AND p."Enabled"
              {string.Join("\n              ", filters)}
              -- An array of objects is a repeater (a line-up, a gallery), not a
              -- vocabulary; jsonb_array_elements_text stringifies those into whole
              -- JSON objects, so the length cap is part of what keeps them out.
              {LooksLikeATag}
            GROUP BY tag.value, p."Slug", i."ContentType", entry.field
            ORDER BY count(*) DESC, tag.value
            LIMIT {MaxTags}
            """;

        return await ReadAsync(db, sql, ct, [.. parameters]);
    }

    /// <summary>
    /// Whether this tenant uses tags at all.
    ///
    /// Asked before the delivery API advertises tag browsing: a documented
    /// endpoint that answers with an empty list on every site that has no tags is
    /// noise in every one of those tenants' API docs.
    /// </summary>
    public static async Task<bool> AnyAsync(DbContext db, Guid tenantId, bool publishedOnly, CancellationToken ct = default)
    {
        // Deliberately not OccurrencesAsync(): this only needs to know whether one
        // row exists, and grouping a tenant's whole content to answer yes/no is
        // work nobody asked for.
        var versionJoin = publishedOnly
            ? """JOIN cms.content_versions v ON v."Id" = i."PublishedVersionId" """
            : """JOIN cms.content_versions v ON v."Id" = COALESCE(i."CurrentDraftVersionId", i."PublishedVersionId") """;
        var statusFilter = publishedOnly ? $"AND i.\"Status\" = '{nameof(ContentStatus.Published)}'" : "";

        var sql = $"""
            SELECT 1
            FROM cms.content_items i
            {versionJoin}
            {ArrayFields}
            WHERE i."TenantId" = @tenantId
              {statusFilter}
              {LooksLikeATag}
            LIMIT 1
            """;

        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(ct);
        }
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            Bind(command, "@tenantId", tenantId);
            return await command.ExecuteScalarAsync(ct) is not null;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// The published items of one collection carrying <paramref name="tag"/>.
    ///
    /// Returned as ids so the caller can keep serving through its own reader —
    /// the point is to filter the existing query, not to build a second path to
    /// content with its own mapping and its own cache.
    /// </summary>
    public static async Task<List<Guid>> ItemIdsWithTagAsync(
        DbContext db,
        Guid tenantId,
        Guid instanceId,
        string contentType,
        string tag,
        string? field,
        CancellationToken ct = default)
    {
        var fieldFilter = string.IsNullOrWhiteSpace(field) ? "" : FieldMatches;
        var sql = $"""
            SELECT DISTINCT i."Id"
            FROM cms.content_items i
            JOIN cms.content_versions v ON v."Id" = i."PublishedVersionId"
            {ArrayFields}
            WHERE i."TenantId" = @tenantId
              AND i."PluginInstanceId" = @instanceId
              AND i."ContentType" = @contentType
              {fieldFilter}
              AND lower(trim(tag.value)) = lower(trim(@tag))
            """;

        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(ct);
        }
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            Bind(command, "@tenantId", tenantId);
            Bind(command, "@instanceId", instanceId);
            Bind(command, "@contentType", contentType);
            if (fieldFilter.Length > 0)
            {
                Bind(command, "@field", field);
            }
            Bind(command, "@tag", tag);

            var ids = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ids.Add(reader.GetGuid(0));
            }
            return ids;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<List<TagOccurrence>> ReadAsync(
        DbContext db, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(ct);
        }
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                Bind(command, name, value);
            }

            var rows = new List<TagOccurrence>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new TagOccurrence(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt32(4)));
            }
            return rows;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void Bind(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
