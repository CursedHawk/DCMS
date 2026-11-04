using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dcms.Functions;

/// <summary>
/// One Dynamic Apps table's records. Each method needs its verb declared with
/// <see cref="UsesTableAttribute"/>: read, create, update or delete. Values are the table's row
/// type with only what to set filled in: a property left null is not written.
/// </summary>
public sealed class Table<TRow>
{
    private readonly Context ctx;
    private readonly string app;
    private readonly string table;

    internal Table(Context ctx, string app, string table) => (this.ctx, this.app, this.table) = (ctx, app, table);

    public async Task<Page<TRow>> Find(Query? query = null) => Context.Read<Page<TRow>>(await Call("find", query))!;

    /// <summary>Null when there is no such record. <paramref name="expand"/>: lookups to return as the record they point at.</summary>
    public async Task<TRow?> Get(string id, params string[] expand) =>
        Context.Read<TRow>(await Call("get", id, expand.Length == 0 ? null : new { expand }));

    public async Task<int> Count(Filter? filter = null) => (await Call("count", filter))?.GetValue<int>() ?? 0;

    public async Task<TRow> Create(TRow values) => Context.Read<TRow>(await Call("create", values))!;

    /// <summary>Only the fields set change. Pass <paramref name="version"/> to refuse a write over someone else's. Null when there is no such record.</summary>
    public async Task<TRow?> Update(string id, TRow values, int? version = null)
    {
        var node = Context.Node(values) as JsonObject ?? [];
        if (version is { } v) node["version"] = v;
        return Context.Read<TRow>(await Call("update", id, node));
    }

    /// <summary>False when there was no such record.</summary>
    public async Task<bool> Delete(string id) => (await Call("delete", id))?.GetValue<bool>() ?? false;

    /// <summary>Many-to-many: false unless both records exist. Needs update.</summary>
    public async Task<bool> Link(string id, string relationship, string targetId) => (await Call("link", id, relationship, targetId))?.GetValue<bool>() ?? false;

    public async Task<bool> Unlink(string id, string relationship, string targetId) => (await Call("unlink", id, relationship, targetId))?.GetValue<bool>() ?? false;

    /// <summary>Up to 500 records. Returns how many changed.</summary>
    public async Task<int> BulkUpdate(IReadOnlyList<string> ids, TRow values) => Affected(await Call("bulkUpdate", ids, values));

    public async Task<int> BulkDelete(IReadOnlyList<string> ids) => Affected(await Call("bulkDelete", ids));

    private static int Affected(JsonNode? result) => result?["affected"]?.GetValue<int>() ?? 0;

    private Task<JsonNode?> Call(string method, params object?[] args) =>
        ctx.CallAsync("db", new JsonObject
        {
            ["path"] = new JsonArray(app, table, method),
            ["args"] = new JsonArray(args.Select(Context.Node).ToArray()),
        });
}

/// <summary>Which records, in what order, which page.</summary>
public sealed record Query
{
    public Filter? Filter { get; init; }
    public IReadOnlyList<Sort>? Sort { get; init; }
    /// <summary>Free text over the table's searchable fields.</summary>
    public string? Search { get; init; }
    /// <summary>The fields to return; id and version always come back.</summary>
    public IReadOnlyList<string>? Select { get; init; }
    /// <summary>Lookups to return as the record they point at; the target table must be declared readable too.</summary>
    public IReadOnlyList<string>? Expand { get; init; }
    public int? Page { get; init; }
    /// <summary>At most 200.</summary>
    public int? PageSize { get; init; }
}

public sealed record Sort(string Field, string Direction = "asc")
{
    public static Sort Asc(string field) => new(field);
    public static Sort Desc(string field) => new(field, "desc");
}

/// <summary>
/// A condition on records: a field (or one lookup away, "company.name") compared, or conditions
/// combined. Build them with the static methods: <c>Filter.Eq("status", "paid")</c>,
/// <c>Filter.And(a, b)</c>.
/// </summary>
public sealed record Filter
{
    public string? Field { get; init; }
    public string? Op { get; init; }
    public JsonNode? Value { get; init; }
    public IReadOnlyList<Filter>? And { get; init; }
    public IReadOnlyList<Filter>? Or { get; init; }
    public Filter? Not { get; init; }

    private static Filter Compare(string field, string op, object? value) => new() { Field = field, Op = op, Value = Context.Node(value) };

    public static Filter Eq(string field, object? value) => Compare(field, "eq", value);
    public static Filter Ne(string field, object? value) => Compare(field, "ne", value);
    public static Filter Gt(string field, object? value) => Compare(field, "gt", value);
    public static Filter Gte(string field, object? value) => Compare(field, "gte", value);
    public static Filter Lt(string field, object? value) => Compare(field, "lt", value);
    public static Filter Lte(string field, object? value) => Compare(field, "lte", value);
    public static Filter In(string field, params object?[] values) => Compare(field, "in", values);
    public static Filter NotIn(string field, params object?[] values) => Compare(field, "nin", values);
    public static Filter Contains(string field, string value) => Compare(field, "contains", value);
    public static Filter StartsWith(string field, string value) => Compare(field, "startsWith", value);
    public static Filter IsNull(string field) => new() { Field = field, Op = "isNull" };
    public static Filter All(params Filter[] filters) => new() { And = filters };
    public static Filter Any(params Filter[] filters) => new() { Or = filters };
    public static Filter Negate(Filter filter) => new() { Not = filter };
}

/// <summary>A page of records. <see cref="Total"/> is counted up to 5,000; <see cref="TotalCapped"/> says when it stopped.</summary>
public sealed record Page<TRow>(IReadOnlyList<TRow> Items, int Total, [property: JsonPropertyName("page")] int Number, int PageSize, bool TotalCapped);

/// <summary>The columns every record has. Generated row types carry them; the host fills them in.</summary>
public abstract record Row
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("version")] public int? Version { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; init; }
    [JsonPropertyName("created_by")] public string? CreatedBy { get; init; }
    [JsonPropertyName("updated_by")] public string? UpdatedBy { get; init; }
}
