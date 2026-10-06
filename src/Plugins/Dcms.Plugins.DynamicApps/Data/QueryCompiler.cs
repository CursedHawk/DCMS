using System.Globalization;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.PluginSdk.Abstractions.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Dcms.Plugins.DynamicApps.Data;

/// <summary>
/// A <see cref="RecordQuery"/> validated against the published model and compiled to SQL.
/// Nothing the caller sends reaches the SQL text: field keys, values and the table are all
/// parameters, and the only identifiers written in are this class's own. Everything a query may
/// ask for is bounded — predicates, nesting, list sizes, page size and depth into the results.
/// </summary>
public sealed class QueryCompiler
{
    public const int MaxPredicates = 20;
    public const int MaxDepth = 5;
    public const int MaxInValues = 100;
    public const int MaxPageSize = 200;
    public const int MaxOffset = 100_000;
    public const int MaxValueChars = 1_000;
    public const int MaxSorts = 3;

    private readonly RuntimeModel _model;
    private readonly RuntimeTable _table;
    private readonly RecordPlane _plane;
    private readonly List<(string Name, object? Value, NpgsqlDbType Type)> _parameters = [];
    private int _predicates;
    private int _aliases;

    public QueryCompiler(RuntimeModel model, RuntimeTable table, RecordPlane plane)
    {
        _model = model;
        _table = table;
        _plane = plane;
    }

    /// <summary>The compiled query: SQL fragments over alias <c>r</c> of <c>apps.records</c>, and fresh parameters on demand.</summary>
    public sealed record Compiled(string Where, string OrderBy, int Limit, int Offset, IReadOnlyList<(string Name, object? Value, NpgsqlDbType Type)> Specs)
    {
        /// <summary>New parameter objects: an NpgsqlParameter belongs to one command, and the page and its count are two.</summary>
        public NpgsqlParameter[] Parameters() =>
            Specs.Select(p => new NpgsqlParameter(p.Name, p.Type) { Value = p.Value ?? DBNull.Value }).ToArray();
    }

    /// <exception cref="ContractValidationException">The query names something the model does not have, or asks for too much.</exception>
    /// <param name="ownerVisitorId">When set, only that site visitor's records match (a table's "own" public access).</param>
    public Compiled Compile(RecordQuery query, Guid tenantId, Guid instanceId, Guid? ownerVisitorId = null)
    {
        if (query.PageSize is < 1 or > MaxPageSize)
        {
            throw Invalid($"pageSize must be between 1 and {MaxPageSize}.");
        }
        if (query.Page < 1 || (long)(query.Page - 1) * query.PageSize > MaxOffset)
        {
            throw Invalid($"page must be at least 1, and at most {MaxOffset:N0} records deep; narrow the query with a filter instead.");
        }
        foreach (var name in query.Select.Where(n => !RecordCodec.SystemNames.Contains(n)))
        {
            Member(_table, name);
        }
        foreach (var name in query.Expand)
        {
            if (!Member(_table, name).IsLookup)
            {
                throw Invalid($"'{name}' is not a lookup; only lookups expand.");
            }
        }

        var where = new List<string>
        {
            $"""r."TenantId" = {Parameter(tenantId, NpgsqlDbType.Uuid)}""",
            $"""r."InstanceId" = {Parameter(instanceId, NpgsqlDbType.Uuid)}""",
            $"""r."TableId" = {Parameter(_table.Id, NpgsqlDbType.Uuid)}""",
        };
        if (ownerVisitorId is { } owner)
        {
            where.Add($"""r."OwnerVisitorId" = {Parameter(owner, NpgsqlDbType.Uuid)}""");
        }
        if (query.Filter is { } filter)
        {
            where.Add(Predicate("r", _table, filter, 1, allowHop: true));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            where.Add(Search(query.Search.Trim()));
        }

        if (query.Sort.Count > MaxSorts)
        {
            throw Invalid($"At most {MaxSorts} sort fields.");
        }
        var order = query.Sort.Count == 0
            ? new List<string> { "r.\"CreatedAt\" DESC" }
            : query.Sort.Select(s => $"{Value("r", _table, s.Field, sorting: true).Sql} {(s.Direction == SortDirection.Desc ? "DESC" : "ASC")} NULLS LAST").ToList();
        order.Add("r.\"Id\""); // a total order, so pages neither repeat nor skip

        return new Compiled(string.Join(" AND ", where), string.Join(", ", order), query.PageSize,
            (query.Page - 1) * query.PageSize, _parameters.ToList());
    }

    // ------------------------------------------------------------------ filters

    private string Predicate(string alias, RuntimeTable table, RecordFilter node, int depth, bool allowHop)
    {
        if (depth > MaxDepth)
        {
            throw Invalid($"Filters nest at most {MaxDepth} deep.");
        }
        var forms = (node.And is not null ? 1 : 0) + (node.Or is not null ? 1 : 0) + (node.Not is not null ? 1 : 0) + (node.Field is not null ? 1 : 0);
        if (forms != 1)
        {
            throw Invalid("Each filter is exactly one of and, or, not, or a field comparison.");
        }
        if (node.And is { } all)
        {
            return Group(all, " AND ", alias, table, depth, allowHop);
        }
        if (node.Or is { } any)
        {
            return Group(any, " OR ", alias, table, depth, allowHop);
        }
        if (node.Not is { } not)
        {
            return $"NOT ({Predicate(alias, table, not, depth + 1, allowHop)})";
        }

        if (++_predicates > MaxPredicates)
        {
            throw Invalid($"A query compares at most {MaxPredicates} fields.");
        }
        var field = node.Field!;
        var dot = field.IndexOf('.');
        if (dot > 0)
        {
            // One hop through a lookup: company.name.
            if (!allowHop)
            {
                throw Invalid($"'{field}': filters follow one lookup at most.");
            }
            var lookup = Member(table, field[..dot]);
            if (!lookup.IsLookup || !_model.ById.TryGetValue(lookup.Lookup!.TargetTableId, out var target))
            {
                throw Invalid($"'{field[..dot]}' is not a lookup of {table.ApiName}.");
            }
            // A filter through a lookup answers questions about the target's rows; on the public
            // site that is only allowed into a table the site may read in full. Otherwise the
            // matches would be an oracle for a private table, or for other visitors' own records.
            if (_plane == RecordPlane.Public && target.Def.Public.Read != PublicRead.All)
            {
                throw Invalid($"{table.ApiName} has no field '{field}'.");
            }
            var t = $"t{++_aliases}";
            var inner = Predicate(t, target, node with { Field = field[(dot + 1)..] }, depth + 1, allowHop: false);
            return $"""
                EXISTS (SELECT 1 FROM apps.records {t} WHERE {t}."TenantId" = {alias}."TenantId" AND {t}."InstanceId" = {alias}."InstanceId"
                AND {t}."TableId" = {Parameter(target.Id, NpgsqlDbType.Uuid)} AND {t}."Id"::text = ({alias}."Data" ->> {Key(lookup)}) AND {inner})
                """;
        }

        var (sql, kind, member) = Value(alias, table, field, sorting: false);
        return Compare(alias, sql, kind, member, node.Op ?? throw Invalid($"'{field}' needs an op."), node.Value, field);
    }

    private string Group(IReadOnlyList<RecordFilter> nodes, string glue, string alias, RuntimeTable table, int depth, bool allowHop) =>
        nodes.Count == 0
            ? throw Invalid("and/or need at least one filter.")
            : "(" + string.Join(glue, nodes.Select(n => Predicate(alias, table, n, depth + 1, allowHop))) + ")";

    private enum Kind { Text, Choice, Multi, Number, Bool, Date, DateTime, Ref, Uuid, Json }

    private static readonly Dictionary<Kind, string[]> Operators = new()
    {
        [Kind.Text] = ["eq", "ne", "in", "nin", "contains", "startsWith", "isNull"],
        [Kind.Choice] = ["eq", "ne", "in", "nin", "isNull"],
        [Kind.Multi] = ["contains", "isNull"],
        [Kind.Number] = ["eq", "ne", "gt", "gte", "lt", "lte", "in", "nin", "isNull"],
        [Kind.Bool] = ["eq", "ne", "isNull"],
        [Kind.Date] = ["eq", "ne", "gt", "gte", "lt", "lte", "isNull"],
        [Kind.DateTime] = ["eq", "ne", "gt", "gte", "lt", "lte", "isNull"],
        [Kind.Ref] = ["eq", "ne", "in", "nin", "isNull"],
        [Kind.Uuid] = ["eq", "ne", "in", "nin"],
        [Kind.Json] = ["isNull"],
    };

    private string Compare(string alias, string sql, Kind kind, RuntimeMember? member, string op, JsonNode? value, string field)
    {
        if (!Operators[kind].Contains(op))
        {
            throw Invalid($"'{field}' does not support '{op}'; it supports {string.Join(", ", Operators[kind])}.");
        }
        if (op == "isNull")
        {
            if (value is not JsonValue v || !v.TryGetValue<bool>(out var isNull))
            {
                throw Invalid($"'{field}' isNull takes true or false.");
            }
            var present = member is null ? $"{sql} IS NOT NULL" : $"""jsonb_exists({alias}."Data", {Key(member)})""";
            return isNull ? $"NOT ({present})" : present;
        }
        if (op is "in" or "nin")
        {
            if (value is not JsonArray items || items.Count is 0 or > MaxInValues)
            {
                throw Invalid($"'{field}' {op} takes a list of 1 to {MaxInValues} values.");
            }
            var (type, values) = (ScalarType(kind), items.Select(i => Scalar(kind, i, field)).ToArray());
            var list = Parameter(ToArray(kind, values), NpgsqlDbType.Array | type);
            return op == "in" ? $"{sql} = ANY({list})" : $"({sql} IS NULL OR NOT ({sql} = ANY({list})))";
        }
        if (kind == Kind.Multi)
        {
            return $"""jsonb_exists({alias}."Data" -> {Key(member!)}, {Parameter(Scalar(Kind.Text, value, field), NpgsqlDbType.Text)})""";
        }
        var p = Parameter(Scalar(kind, value, field), ScalarType(kind));
        return op switch
        {
            "eq" => $"{sql} = {p}",
            "ne" => $"{sql} IS DISTINCT FROM {p}",
            "gt" => $"{sql} > {p}",
            "gte" => $"{sql} >= {p}",
            "lt" => $"{sql} < {p}",
            "lte" => $"{sql} <= {p}",
            "contains" => $"strpos(lower({sql}), lower({p})) > 0",
            _ => $"starts_with(lower({sql}), lower({p}))",
        };
    }

    // ------------------------------------------------------------------ values

    private (string Sql, Kind Kind, RuntimeMember? Member) Value(string alias, RuntimeTable table, string field, bool sorting)
    {
        switch (field)
        {
            case "id":
                return ($"{alias}.\"Id\"", Kind.Uuid, null);
            case "created_at":
                return ($"{alias}.\"CreatedAt\"", Kind.DateTime, null);
            case "updated_at":
                return ($"{alias}.\"UpdatedAt\"", Kind.DateTime, null);
            case "created_by" when _plane != RecordPlane.Public:
                return ($"{alias}.\"CreatedBy\"", Kind.Text, null);
        }
        if (field.Contains('.'))
        {
            throw Invalid(sorting ? $"'{field}': sort by this table's own fields." : $"'{field}': unknown field.");
        }
        var member = Member(table, field);
        var kind = KindOf(member);
        if (sorting && kind is Kind.Multi or Kind.Json)
        {
            throw Invalid($"'{field}' cannot be sorted on.");
        }
        var text = $"({alias}.\"Data\" ->> {Key(member)})";
        var sql = kind switch
        {
            Kind.Number => $"{text}::numeric",
            Kind.Bool => $"{text}::boolean",
            Kind.Date => $"{text}::date",
            Kind.DateTime => $"{text}::timestamptz",
            _ => text,
        };
        return (sql, kind, member);
    }

    private static Kind KindOf(RuntimeMember member) => member.IsLookup
        ? Kind.Ref
        : member.Field!.Type switch
        {
            FieldType.Text or FieldType.LongText or FieldType.Email or FieldType.Url => Kind.Text,
            FieldType.Choice => Kind.Choice,
            FieldType.MultiChoice => Kind.Multi,
            FieldType.Integer or FieldType.Decimal => Kind.Number,
            FieldType.Boolean => Kind.Bool,
            FieldType.Date => Kind.Date,
            FieldType.DateTime => Kind.DateTime,
            FieldType.Media => Kind.Ref,
            _ => Kind.Json,
        };

    private static NpgsqlDbType ScalarType(Kind kind) => kind switch
    {
        Kind.Number => NpgsqlDbType.Numeric,
        Kind.Bool => NpgsqlDbType.Boolean,
        Kind.Date => NpgsqlDbType.Date,
        Kind.DateTime => NpgsqlDbType.TimestampTz,
        Kind.Uuid => NpgsqlDbType.Uuid,
        _ => NpgsqlDbType.Text,
    };

    private static object Scalar(Kind kind, JsonNode? value, string field)
    {
        var v = value as JsonValue;
        string? text = v is not null && v.TryGetValue<string>(out var s) ? s : null;
        object? result = kind switch
        {
            Kind.Number => v is not null && v.TryGetValue<decimal>(out var n) ? n : null,
            Kind.Bool => v is not null && v.TryGetValue<bool>(out var b) ? b : null,
            Kind.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
            Kind.DateTime => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.UtcDateTime : null,
            Kind.Uuid => Guid.TryParse(text, out var g) ? g : null,
            // Lookups and media ids are stored in Guid's "d" form; match whatever case was sent.
            Kind.Ref => Guid.TryParse(text, out var r) ? r.ToString() : null,
            _ => text is { Length: <= MaxValueChars } ? text : null,
        };
        return result ?? throw Invalid($"'{field}': {value?.ToJsonString() ?? "null"} is not a valid value for this field.");
    }

    private static Array ToArray(Kind kind, object[] values) => kind switch
    {
        Kind.Number => values.Cast<decimal>().ToArray(),
        Kind.Uuid => values.Cast<Guid>().ToArray(),
        _ => values.Cast<string>().ToArray(),
    };

    private string Search(string text)
    {
        if (text.Length > 200)
        {
            throw Invalid("search is at most 200 characters.");
        }
        var members = _table.Members.Where(m => m.Field is { Searchable: true } && KindOf(m) == Kind.Text && RecordCodec.Visible(m, _plane)).ToList();
        if (members.Count == 0 && _table.Primary is { } primary && KindOf(primary) == Kind.Text && RecordCodec.Visible(primary, _plane))
        {
            members.Add(primary);
        }
        if (members.Count == 0)
        {
            throw Invalid($"{_table.ApiName} has no searchable text fields.");
        }
        var p = Parameter(text, NpgsqlDbType.Text);
        return "(" + string.Join(" OR ", members.Select(m => $"""strpos(lower(r."Data" ->> {Key(m)}), lower({p})) > 0""")) + ")";
    }

    // ------------------------------------------------------------------ helpers

    private RuntimeMember Member(RuntimeTable table, string name) =>
        table.ByName.TryGetValue(name, out var member) && RecordCodec.Visible(member, _plane)
            ? member
            : throw Invalid($"{table.ApiName} has no field '{name}'.");

    private string Key(RuntimeMember member) => Parameter(member.Key, NpgsqlDbType.Text);

    private string Parameter(object value, NpgsqlDbType type)
    {
        var name = $"p{_parameters.Count}";
        _parameters.Add((name, value, type));
        return "@" + name;
    }

    private static ContractValidationException Invalid(string message) => new(message);
}
