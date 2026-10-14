using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Shared.Kernel.Views;

namespace Dcms.Shared.Data.Cms;

/// <summary>How a content field's value is compared (#278).</summary>
public enum ContentFieldKind { Text, Number, Boolean, DateTime, Tags, Reference }

/// <summary>Where a field's value sits in an item's data, and how it compares.</summary>
/// <param name="Path">Keys from the root of the version's data: <c>["price"]</c>, or <c>["custom", "size"]</c> for a tenant-defined field.</param>
public sealed record ContentField(IReadOnlyList<string> Path, ContentFieldKind Kind);

/// <summary>
/// A view's filter (<see cref="ViewFilter"/>) as SQL over a collection page (#278): alias
/// <c>i</c> is <c>cms.content_items</c>, <c>v."DataJson"</c> the item's draft (else published)
/// data. Field names and values are always parameters; only this class's own fragments are SQL.
/// </summary>
/// <remarks>
/// System fields: <c>_slug</c>, <c>_status</c>, <c>_title</c>, <c>_updatedAt</c>, <c>_publishedAt</c>.
/// Every other name must be in the caller's field catalogue — the type's declared fields and its
/// tenant-defined ones — or the filter is refused.
/// </remarks>
public sealed class ContentFilterSql(IReadOnlyDictionary<string, ContentField> fields, string titleSql, List<(string, object?)> parameters)
{
    public static readonly IReadOnlyDictionary<string, ContentFieldKind> SystemFields = new Dictionary<string, ContentFieldKind>
    {
        ["_slug"] = ContentFieldKind.Text,
        ["_status"] = ContentFieldKind.Text,
        ["_title"] = ContentFieldKind.Text,
        ["_updatedAt"] = ContentFieldKind.DateTime,
        ["_publishedAt"] = ContentFieldKind.DateTime,
    };

    private int _n;

    /// <summary>Whether a field reads the version's data, so the query must join it.</summary>
    public static bool ReadsData(ViewFilter? filter) => filter is not null && (
        (filter.Field is { } f && (f == "_title" || !SystemFields.ContainsKey(f)))
        || (filter.And ?? filter.Or ?? []).Any(ReadsData)
        || ReadsData(filter.Not));

    /// <exception cref="ViewDefinitionException">A field the collection does not have, or an operator its type does not take.</exception>
    public string Compile(ViewFilter filter)
    {
        if (filter.And is { } and) return and.Count == 0 ? "TRUE" : "(" + string.Join(" AND ", and.Select(Compile)) + ")";
        if (filter.Or is { } or) return or.Count == 0 ? "FALSE" : "(" + string.Join(" OR ", or.Select(Compile)) + ")";
        if (filter.Not is { } not) return $"(NOT COALESCE({Compile(not)}, FALSE))";
        return Comparison(filter.Field!, filter.Op!, filter.Value);
    }

    /// <summary>The SQL expression a field's value is read as: text, numeric, or the jsonb itself for tags.</summary>
    public string Expression(string field, out ContentFieldKind kind)
    {
        if (SystemFields.TryGetValue(field, out kind))
        {
            return field switch
            {
                "_slug" => """i."Slug" """,
                "_status" => """i."Status" """,
                "_title" => titleSql,
                "_updatedAt" => """i."UpdatedAt" """,
                _ => """i."PublishedAt" """,
            };
        }
        if (!fields.TryGetValue(field, out var def)) throw new ViewDefinitionException($"This collection has no field '{field}'.");
        kind = def.Kind;
        var path = Param(def.Path.ToArray());
        return kind switch
        {
            ContentFieldKind.Tags => $"""(v."DataJson" #> {path})""",
            // Numbers arrive as JSON numbers or, from older forms, numeric strings; anything else
            // is no number rather than an error that fails the whole list.
            ContentFieldKind.Number => $"""(CASE WHEN (v."DataJson" #>> {path}) ~ '^\s*-?[0-9]+(\.[0-9]+)?\s*$' THEN (v."DataJson" #>> {path})::numeric END)""",
            _ => $"""NULLIF(v."DataJson" #>> {path}, '')""",
        };
    }

    private string Comparison(string field, string op, JsonNode? value)
    {
        var expr = Expression(field, out var kind);
        if (op == "isNull" || (op is "eq" or "ne" && value is null))
        {
            var wantNull = op == "isNull" ? value?.GetValueKind() != JsonValueKind.False : op == "eq";
            var isNull = kind == ContentFieldKind.Tags
                ? $"""(jsonb_typeof({expr}) IS DISTINCT FROM 'array' OR {expr} = '[]'::jsonb)"""
                : $"({expr} IS NULL)";
            return wantNull ? isNull : $"(NOT {isNull})";
        }

        switch (kind)
        {
            case ContentFieldKind.Tags:
                return op switch
                {
                    "eq" or "contains" => $"(COALESCE({expr} ? {Param(Text(value))}, FALSE))",
                    "ne" => $"(NOT COALESCE({expr} ? {Param(Text(value))}, FALSE))",
                    "in" => $"(COALESCE({expr} ?| {Param(Texts(value))}, FALSE))",
                    "nin" => $"(NOT COALESCE({expr} ?| {Param(Texts(value))}, FALSE))",
                    _ => throw Unsupported(field, op),
                };
            case ContentFieldKind.Boolean:
                if (op is not ("eq" or "ne")) throw Unsupported(field, op);
                var truth = value?.GetValueKind() == JsonValueKind.True || (value?.GetValueKind() == JsonValueKind.String && value.GetValue<string>() == "true");
                // A box never ticked has no value, and means false.
                var test = $"(COALESCE({expr}, 'false') = {Param(truth ? "true" : "false")})";
                return op == "eq" ? test : $"(NOT {test})";
            case ContentFieldKind.Number:
                return Ordered(expr, op, field, value, v => Param(Number(v)), v => Param(Numbers(v)));
            case ContentFieldKind.DateTime when field is "_updatedAt" or "_publishedAt":
                return Ordered(expr, op, field, value, v => Param(Instant(v)), _ => throw Unsupported(field, op));
            case ContentFieldKind.DateTime:
                // ponytail: stored dates compare as ISO text (what the editor writes); a date typed
                // in another shape sorts wrong rather than failing the list. Cast if that appears.
                return Ordered(expr, op, field, value, v => Param(Text(v)), v => Param(Texts(v)));
            default:
                return op switch
                {
                    "contains" => $"({expr} ILIKE {Param("%" + Like(Text(value)) + "%")})",
                    "startsWith" => $"({expr} ILIKE {Param(Like(Text(value)) + "%")})",
                    _ => Ordered(expr, op, field, value, v => Param(Text(v)), v => Param(Texts(v))),
                };
        }
    }

    private static string Ordered(string expr, string op, string field, JsonNode? value, Func<JsonNode?, string> one, Func<JsonNode?, string> many) => op switch
    {
        "eq" => $"({expr} = {one(value)})",
        "ne" => $"({expr} IS DISTINCT FROM {one(value)})",
        "gt" => $"({expr} > {one(value)})",
        "gte" => $"({expr} >= {one(value)})",
        "lt" => $"({expr} < {one(value)})",
        "lte" => $"({expr} <= {one(value)})",
        "in" => $"({expr} = ANY({many(value)}))",
        "nin" => $"(NOT COALESCE({expr} = ANY({many(value)}), FALSE))",
        _ => throw Unsupported(field, op),
    };

    private string Param(object value)
    {
        var name = $"@vf{_n++}";
        parameters.Add((name, value));
        return name;
    }

    private static string Like(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string Text(JsonNode? v) => v switch
    {
        JsonValue x when x.GetValueKind() == JsonValueKind.String => x.GetValue<string>(),
        JsonValue x => x.ToJsonString(),
        _ => throw new ViewDefinitionException("This condition needs one value."),
    };

    private static string[] Texts(JsonNode? v) => v is JsonArray list
        ? list.Select(Text).ToArray()
        : throw new ViewDefinitionException("This condition needs a list of values.");

    private static decimal Number(JsonNode? v) =>
        decimal.TryParse(Text(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : throw new ViewDefinitionException("This condition needs a number.");

    private static decimal[] Numbers(JsonNode? v) => v is JsonArray list
        ? list.Select(Number).ToArray()
        : throw new ViewDefinitionException("This condition needs a list of numbers.");

    private static DateTimeOffset Instant(JsonNode? v) =>
        DateTimeOffset.TryParse(Text(v), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d : throw new ViewDefinitionException("This condition needs a date.");

    private static ViewDefinitionException Unsupported(string field, string op) =>
        new($"'{op}' does not apply to the field '{field}'.");
}
