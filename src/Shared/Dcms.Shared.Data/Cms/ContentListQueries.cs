using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Cms;

/// <summary>
/// One page of a collection, filtered and searchable, without loading anybody's draft.
///
/// <para><b>Why this exists.</b> The console listed a collection by fetching every item in it
/// <i>with its full draft JSON</i> — the only way to show a real title rather than a slug — and
/// then filtered in the browser. That is fine for the twelve gigs a venue has and is megabytes
/// for a blog: the payload grows with what authors have written, not with how many rows there
/// are, so it degrades quietly and in proportion to how much the tenant has used the product.
/// Projecting the title here costs one <c>-&gt;&gt;</c> per row and lets the page be a page.</para>
///
/// <para><b>Raw ADO, like <see cref="TagQueries"/> next door and for the same reason.</b> The
/// title comes out of a jsonb column under a key that is only known at query time, which no
/// LINQ expression can say and <c>SqlQuery</c> cannot project. Every input is a bound
/// parameter; the tenant is filtered explicitly rather than by the ambient query filter,
/// because raw SQL does not go through it.</para>
/// </summary>
public static class ContentListQueries
{
    /// <summary>Enough for a long scroll, small enough that no single response is a download.</summary>
    public const int MaxPageSize = 200;

    public const int DefaultPageSize = 50;

    /// <param name="Title">
    /// The item's first textual field, falling back to the slug. Tags are stripped: a RichText
    /// title field is markup, and a list that renders it raw shows an author their own
    /// <c>&lt;p&gt;</c>.
    /// </param>
    /// <param name="ScheduledPublishAt">
    /// When a queued publish will fire. Not a status — the enum is Draft/Published/Archived and
    /// an item waiting to go live is genuinely still a draft — but it is what the list shows and
    /// filters on, so it is reported alongside.
    /// </param>
    public sealed record ContentRow(
        Guid Id,
        string ContentType,
        string Slug,
        string Title,
        string Status,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? PublishedAt,
        DateTimeOffset? ScheduledPublishAt);

    /// <param name="UpdatedAt">The last row's timestamp; paging continues strictly before it.</param>
    /// <param name="Id">
    /// The tiebreak. Two items saved in the same tick are ordinary — a bulk import writes a
    /// hundred — and a cursor on the timestamp alone would skip or repeat them across the break.
    /// </param>
    public sealed record Cursor(DateTimeOffset UpdatedAt, Guid Id);

    public sealed record Page(IReadOnlyList<ContentRow> Items, Cursor? NextCursor, int Total);

    /// <summary>
    /// The synthetic status the console filters on, which no column holds.
    /// </summary>
    public const string ScheduledStatus = "Scheduled";

    /// <summary>
    /// A page of one collection.
    /// </summary>
    /// <param name="titleField">
    /// The field to read the title from, chosen by the caller from the plugin's declared fields.
    /// Null falls back to the slug, which is what a content type with no textual field has.
    /// </param>
    /// <param name="itemIds">
    /// When given, the only items to consider — how the tag filter is applied, since which items
    /// carry a tag is a question <see cref="TagQueries"/> already answers.
    /// </param>
    public static async Task<Page> PageAsync(
        DbContext db,
        Guid tenantId,
        Guid instanceId,
        string? contentType,
        string? titleField,
        string? search,
        string? status,
        IReadOnlyCollection<Guid>? itemIds,
        Cursor? after,
        int limit,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, MaxPageSize);

        // The title expression, used by both the projection and the search so the two can never
        // disagree about what a row is called.
        var title = titleField is null
            ? """i."Slug" """
            : """COALESCE(NULLIF(btrim(regexp_replace(v."DataJson" ->> @titleField, '<[^>]*>', '', 'g')), ''), i."Slug")""";

        const string PendingSchedule = """
            (SELECT min(sp."PublishAt") FROM cms.scheduled_publishes sp
              WHERE sp."ItemId" = i."Id" AND sp."Status" = 'Pending')
            """;

        var parameters = new List<(string, object?)>
        {
            ("@tenantId", tenantId),
            ("@instanceId", instanceId),
        };
        var filters = new List<string>();

        if (titleField is not null)
        {
            parameters.Add(("@titleField", titleField));
        }
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            filters.Add("""AND i."ContentType" = @contentType""");
            parameters.Add(("@contentType", contentType));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            // ILIKE with the wildcards bound into the value, never into the SQL. `\` escapes
            // itself first so a search for "100%" is a search for the characters rather than a
            // pattern that matches everything.
            filters.Add($"""AND (i."Slug" ILIKE @search OR {title} ILIKE @search)""");
            parameters.Add(("@search", $"%{Escape(search.Trim())}%"));
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status == ScheduledStatus)
            {
                filters.Add($"""AND i."Status" <> 'Published' AND {PendingSchedule} IS NOT NULL""");
            }
            else
            {
                // Mirrors the console's rule exactly: a queued publish takes an unpublished item
                // out of its own status and into "Scheduled", so filtering for Draft must not
                // return the drafts that are already on their way out.
                filters.Add($"""
                    AND i."Status" = @status
                      AND (i."Status" = 'Published' OR {PendingSchedule} IS NULL)
                    """);
                parameters.Add(("@status", status));
            }
        }
        if (itemIds is not null)
        {
            // An empty set has to become a filter that matches nothing, not an absent filter:
            // "no item carries this tag" and "no tag was asked for" are opposite answers.
            filters.Add("""AND i."Id" = ANY(@itemIds)""");
            parameters.Add(("@itemIds", itemIds.ToArray()));
        }

        var where = $"""
            WHERE i."TenantId" = @tenantId
              AND i."PluginInstanceId" = @instanceId
              {string.Join("\n              ", filters)}
            """;

        const string VersionJoin = """
            LEFT JOIN cms.content_versions v
                   ON v."Id" = COALESCE(i."CurrentDraftVersionId", i."PublishedVersionId")
            """;

        var keyset = after is null
            ? string.Empty
            : """AND (i."UpdatedAt", i."Id") < (@cursorAt, @cursorId)""";
        if (after is { } cursor)
        {
            parameters.Add(("@cursorAt", cursor.UpdatedAt));
            parameters.Add(("@cursorId", cursor.Id));
        }

        var sql = $"""
            SELECT i."Id", i."ContentType", i."Slug", {title} AS title, i."Status",
                   i."UpdatedAt", i."PublishedAt", {PendingSchedule} AS scheduled_at
            FROM cms.content_items i
            {VersionJoin}
            {where}
              {keyset}
            ORDER BY i."UpdatedAt" DESC, i."Id" DESC
            LIMIT {take + 1}
            """;

        var rows = await QueryAsync(db, sql, parameters, reader => new ContentRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)), ct);

        var hasMore = rows.Count > take;
        var page = hasMore ? rows.Take(take).ToList() : rows;

        // Counted with the same filters and without the cursor, because "37 items" has to mean
        // "37 match what you asked for", not "37 are left below where you have scrolled".
        var countSql = $"""
            SELECT count(*)::int
            FROM cms.content_items i
            {VersionJoin}
            {where}
            """;

        var total = (await QueryAsync(db, countSql, parameters, r => r.GetInt32(0), ct)).FirstOrDefault();

        return new Page(
            page,
            hasMore ? new Cursor(page[^1].UpdatedAt, page[^1].Id) : null,
            total);
    }

    /// <param name="Instance">The plugin instance the item belongs to, for a workspace-wide list.</param>
    /// <param name="PublishAt">When the worker will publish it.</param>
    public sealed record ScheduledRow(
        Guid ScheduleId,
        Guid ItemId,
        Guid InstanceId,
        string InstanceName,
        string PluginId,
        string ContentType,
        string Slug,
        string Title,
        string Status,
        DateTimeOffset PublishAt);

    /// <summary>
    /// Everything queued to publish across the whole workspace, soonest first.
    ///
    /// <para>The scheduler has always worked and nothing has ever shown its queue. An author who
    /// scheduled a post could see "Scheduled" beside that one item, in that one collection, if
    /// they went looking — and had no way at all to answer "what is going out this week", which
    /// is the question the feature exists to serve. This is the only read in the CMS that
    /// deliberately crosses plugin instances.</para>
    ///
    /// <para>The title is read from the <b>scheduled version</b> rather than the current draft:
    /// this list says what is going to be published, and an author who has since kept writing
    /// should see the headline that is actually queued, not the one they are working on.</para>
    ///
    /// <para>Which field holds the title differs per content type, and a workspace-wide list
    /// spans several. The caller passes the mapping it read from the plugin catalogue as two
    /// parallel arrays, joined here — one query rather than one per content type, and no
    /// interpolation of a field name into SQL.</para>
    /// </summary>
    /// <param name="titleFields">
    /// <c>pluginId:contentType</c> → the field holding that type's title. A type not in the map
    /// falls back to its slug, which is also what a type with no textual field gets.
    /// </param>
    public static async Task<IReadOnlyList<ScheduledRow>> ScheduledAsync(
        DbContext db,
        Guid tenantId,
        IReadOnlyDictionary<string, string> titleFields,
        int limit,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, MaxPageSize);

        const string Sql = """
            SELECT sp."Id", i."Id", pi."Id", pi."Name", pi."PluginId",
                   i."ContentType", i."Slug",
                   COALESCE(
                       NULLIF(btrim(regexp_replace(v."DataJson" ->> tf.field, '<[^>]*>', '', 'g')), ''),
                       i."Slug") AS title,
                   i."Status", sp."PublishAt"
            FROM cms.scheduled_publishes sp
            JOIN cms.content_items i ON i."Id" = sp."ItemId"
            JOIN plugins.plugin_instances pi ON pi."Id" = i."PluginInstanceId"
            LEFT JOIN cms.content_versions v ON v."Id" = sp."VersionId"
            LEFT JOIN LATERAL (
                SELECT m.field
                FROM unnest(@types, @fields) AS m(type, field)
                WHERE m.type = pi."PluginId" || ':' || i."ContentType"
                LIMIT 1
            ) tf ON TRUE
            WHERE sp."TenantId" = @tenantId
              AND sp."Status" = 'Pending'
            ORDER BY sp."PublishAt" ASC, sp."Id" ASC
            LIMIT @take
            """;

        var parameters = new List<(string, object?)>
        {
            ("@tenantId", tenantId),
            ("@types", titleFields.Keys.ToArray()),
            ("@fields", titleFields.Values.ToArray()),
            ("@take", take),
        };

        return await QueryAsync(db, Sql, parameters, r => new ScheduledRow(
            r.GetGuid(0),
            r.GetGuid(1),
            r.GetGuid(2),
            r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.GetString(7),
            r.GetString(8),
            r.GetFieldValue<DateTimeOffset>(9)), ct);
    }

    /// <summary>
    /// The items of one collection carrying <paramref name="tag"/>, from the DRAFT version.
    ///
    /// <para>Distinct from <see cref="TagQueries.ItemIdsWithTagAsync"/>, which reads the
    /// published version because it serves visitors. An author filtering their own list by a tag
    /// they typed this morning and have not published would otherwise get nothing back, which
    /// reads as "the filter is broken" rather than as "that tag is not live yet".</para>
    /// </summary>
    public static async Task<List<Guid>> DraftItemIdsWithTagAsync(
        DbContext db,
        Guid tenantId,
        Guid instanceId,
        string? contentType,
        string tag,
        CancellationToken ct = default)
    {
        var typeFilter = string.IsNullOrWhiteSpace(contentType)
            ? string.Empty
            : """AND i."ContentType" = @contentType""";

        var sql = $"""
            SELECT DISTINCT i."Id"
            FROM cms.content_items i
            JOIN cms.content_versions v
              ON v."Id" = COALESCE(i."CurrentDraftVersionId", i."PublishedVersionId")
            CROSS JOIN LATERAL (
                SELECT e.value AS arr
                FROM jsonb_each(v."DataJson") e
                WHERE jsonb_typeof(e.value) = 'array'
                UNION ALL
                SELECT n.value AS arr
                FROM jsonb_each(v."DataJson") e
                CROSS JOIN LATERAL jsonb_each(e.value) n
                WHERE jsonb_typeof(e.value) = 'object' AND jsonb_typeof(n.value) = 'array'
            ) entry
            CROSS JOIN LATERAL jsonb_array_elements_text(entry.arr) AS tag
            WHERE i."TenantId" = @tenantId
              AND i."PluginInstanceId" = @instanceId
              {typeFilter}
              AND lower(trim(tag.value)) = lower(trim(@tag))
            """;

        var parameters = new List<(string, object?)>
        {
            ("@tenantId", tenantId),
            ("@instanceId", instanceId),
            ("@tag", tag),
        };
        if (typeFilter.Length > 0)
        {
            parameters.Add(("@contentType", contentType));
        }

        return await QueryAsync(db, sql, parameters, r => r.GetGuid(0), ct);
    }

    /// <summary>
    /// Wildcards bound into a value are data; wildcards a user typed are not a pattern they
    /// asked for. The backslash goes first or it would escape the escapes.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>
    /// Runs one query and projects every row.
    ///
    /// <para>Opens the connection only if it was closed, and closes only what it opened — the
    /// same bookkeeping <see cref="TagQueries"/> does. <c>CommandBehavior.CloseConnection</c>
    /// would be shorter and wrong: this runs twice on one DbContext, and inside an EF
    /// transaction it would close a connection EF still holds.</para>
    /// </summary>
    private static async Task<List<T>> QueryAsync<T>(
        DbContext db,
        string sql,
        IReadOnlyList<(string Name, object? Value)> parameters,
        Func<DbDataReader, T> map,
        CancellationToken ct)
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
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            var rows = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(map(reader));
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
}
