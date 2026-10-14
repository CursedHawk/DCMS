using System.Text.Json.Nodes;

namespace Dcms.Shared.Kernel.Views;

/// <summary>
/// What a saved table view shows (#278): which columns in which order, the sort, and a filter.
/// The same shape for content lists, Dynamic Apps records and plugin data sets; field names are
/// the table's own keys, checked by whatever runs the query, not here.
/// </summary>
public sealed record ViewDefinition
{
    public const int MaxColumns = 50;
    public const int MaxSorts = 3;

    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<ViewSort> Sort { get; init; } = [];
    public ViewFilter? Filter { get; init; }

    /// <exception cref="ViewDefinitionException">Too much, or a filter that is not one.</exception>
    public void Validate()
    {
        if (Columns.Count > MaxColumns) throw new ViewDefinitionException($"A view shows at most {MaxColumns} columns.");
        if (Columns.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 200)) throw new ViewDefinitionException("A column is not a field name.");
        if (Sort.Count > MaxSorts) throw new ViewDefinitionException($"A view sorts by at most {MaxSorts} fields.");
        foreach (var sort in Sort)
        {
            if (string.IsNullOrWhiteSpace(sort.Field) || sort.Field.Length > 200) throw new ViewDefinitionException("A sort is not a field name.");
            if (sort.Direction is not ("asc" or "desc")) throw new ViewDefinitionException("A sort's direction is asc or desc.");
        }
        Filter?.Validate();
    }
}

public sealed record ViewSort(string Field, string Direction = "asc")
{
    public bool Descending => Direction == "desc";
}

/// <summary>
/// One node of a filter: exactly one of <see cref="And"/>, <see cref="Or"/>, <see cref="Not"/>,
/// or a comparison (<see cref="Field"/> + <see cref="Op"/> + <see cref="Value"/>). The shape of
/// Dynamic Apps' RecordFilter, so a view's filter reaches its records unchanged.
/// </summary>
/// <remarks>
/// Operators: <c>eq ne gt gte lt lte in nin contains startsWith isNull</c>. <c>in</c>/<c>nin</c>
/// take a list, <c>isNull</c> true or false. Which apply to a field depends on its type.
/// </remarks>
public sealed record ViewFilter
{
    public const int MaxPredicates = 20;
    public const int MaxDepth = 5;
    public const int MaxInValues = 100;
    public const int MaxValueChars = 1_000;

    public static readonly IReadOnlySet<string> Ops = new HashSet<string>(StringComparer.Ordinal)
    {
        "eq", "ne", "gt", "gte", "lt", "lte", "in", "nin", "contains", "startsWith", "isNull",
    };

    public IReadOnlyList<ViewFilter>? And { get; init; }
    public IReadOnlyList<ViewFilter>? Or { get; init; }
    public ViewFilter? Not { get; init; }
    public string? Field { get; init; }
    public string? Op { get; init; }
    public JsonNode? Value { get; init; }

    /// <exception cref="ViewDefinitionException">Not one kind of node, an unknown operator, or too big.</exception>
    public void Validate()
    {
        var predicates = 0;
        Walk(this, 1, ref predicates);
    }

    private static void Walk(ViewFilter node, int depth, ref int predicates)
    {
        if (depth > MaxDepth) throw new ViewDefinitionException($"A filter nests at most {MaxDepth} deep.");
        var kinds = (node.And is null ? 0 : 1) + (node.Or is null ? 0 : 1) + (node.Not is null ? 0 : 1) + (node.Field is null ? 0 : 1);
        if (kinds != 1) throw new ViewDefinitionException("Each filter node is exactly one of and, or, not, or a field comparison.");

        if (node.Field is not null)
        {
            if (++predicates > MaxPredicates) throw new ViewDefinitionException($"A filter has at most {MaxPredicates} conditions.");
            if (node.Field.Length is 0 or > 200) throw new ViewDefinitionException("A condition's field is not a field name.");
            if (node.Op is null || !Ops.Contains(node.Op)) throw new ViewDefinitionException($"'{node.Op}' is not a filter operator.");
            if (node.Value is JsonArray list && list.Count > MaxInValues) throw new ViewDefinitionException($"A list has at most {MaxInValues} values.");
            if (node.Value?.ToJsonString().Length > MaxValueChars * 4) throw new ViewDefinitionException("A condition's value is too long.");
            return;
        }
        if (node.Not is not null)
        {
            Walk(node.Not, depth + 1, ref predicates);
            return;
        }
        var children = node.And ?? node.Or!;
        foreach (var child in children)
        {
            Walk(child, depth + 1, ref predicates);
        }
    }
}

public sealed class ViewDefinitionException(string message) : Exception(message);
