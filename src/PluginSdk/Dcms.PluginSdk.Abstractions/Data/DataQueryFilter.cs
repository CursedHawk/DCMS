using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Kernel.Views;

namespace Dcms.PluginSdk.Abstractions.Data;

/// <summary>
/// Applies a saved view's filter (<see cref="DataQuery.Where"/>) to a LINQ query (#278): name
/// each filterable column's property, and the filter becomes a <c>Where</c> the provider
/// translates (EF Core turns it into SQL).
/// </summary>
/// <example>
/// <code>
/// rows = DataQueryFilter.Apply(rows, query.Where, new Dictionary&lt;string, LambdaExpression&gt;
/// {
///     ["form"] = (Expression&lt;Func&lt;FormSubmission, string&gt;&gt;)(s =&gt; s.FormName),
///     ["submittedAt"] = (Expression&lt;Func&lt;FormSubmission, DateTimeOffset&gt;&gt;)(s =&gt; s.SubmittedAt),
/// });
/// </code>
/// </example>
public static class DataQueryFilter
{
    public static IQueryable<T> Apply<T>(IQueryable<T> rows, ViewFilter? filter, IReadOnlyDictionary<string, LambdaExpression> columns)
    {
        if (filter is null) return rows;
        var row = Expression.Parameter(typeof(T), "row");
        return rows.Where(Expression.Lambda<Func<T, bool>>(Node(filter, row, columns), row));
    }

    private static Expression Node(ViewFilter f, ParameterExpression row, IReadOnlyDictionary<string, LambdaExpression> columns)
    {
        if (f.And is { } and) return and.Select(x => Node(x, row, columns)).Aggregate((Expression)Expression.Constant(true), Expression.AndAlso);
        if (f.Or is { } or) return or.Count == 0 ? Expression.Constant(false) : or.Select(x => Node(x, row, columns)).Aggregate(Expression.OrElse);
        if (f.Not is { } not) return Expression.Not(Node(not, row, columns));

        if (!columns.TryGetValue(f.Field!, out var selector))
        {
            throw new ContractValidationException($"The column '{f.Field}' cannot be filtered on.");
        }
        var value = new Rebind(selector.Parameters[0], row).Visit(selector.Body);
        var type = value.Type;
        var nullable = !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
        Expression IsNull() => nullable ? Expression.Equal(value, Expression.Constant(null, type)) : Expression.Constant(false);

        switch (f.Op)
        {
            case "isNull":
                return f.Value?.GetValueKind() == JsonValueKind.False ? Expression.Not(IsNull()) : IsNull();
            case "contains" or "startsWith" when type == typeof(string):
                var text = Expression.Constant(Text(f.Value).ToLowerInvariant());
                var lower = Expression.Call(value, nameof(string.ToLower), Type.EmptyTypes);
                var test = Expression.Call(lower, f.Op == "contains" ? nameof(string.Contains) : nameof(string.StartsWith), Type.EmptyTypes, text);
                return Expression.AndAlso(Expression.NotEqual(value, Expression.Constant(null, type)), test);
            case "in" or "nin":
                var list = f.Value as JsonArray ?? throw new ContractValidationException("This condition needs a list of values.");
                var any = list.Count == 0
                    ? (Expression)Expression.Constant(false)
                    : list.Select(v => (Expression)Expression.Equal(value, Constant(v, type))).Aggregate(Expression.OrElse);
                return f.Op == "in" ? any : Expression.Not(any);
            case "eq":
                return Expression.Equal(value, Constant(f.Value, type));
            case "ne":
                return Expression.NotEqual(value, Constant(f.Value, type));
            case "gt":
                return Expression.GreaterThan(value, Constant(f.Value, type));
            case "gte":
                return Expression.GreaterThanOrEqual(value, Constant(f.Value, type));
            case "lt":
                return Expression.LessThan(value, Constant(f.Value, type));
            case "lte":
                return Expression.LessThanOrEqual(value, Constant(f.Value, type));
            default:
                throw new ContractValidationException($"'{f.Op}' does not apply to the column '{f.Field}'.");
        }
    }

    /// <summary>A filter value as the column's own type, so the comparison translates.</summary>
    private static ConstantExpression Constant(JsonNode? node, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (node is null) return Expression.Constant(null, type);
        object converted;
        try
        {
            var text = Text(node);
            converted = target == typeof(string) ? text
                : target == typeof(bool) ? node.GetValueKind() == JsonValueKind.True || text == "true"
                : target == typeof(DateTimeOffset) ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                : target == typeof(DateTime) ? DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
                : target == typeof(Guid) ? Guid.Parse(text)
                : target.IsEnum ? Enum.Parse(target, text, ignoreCase: true)
                : Convert.ChangeType(text, target, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new ContractValidationException($"'{node.ToJsonString()}' is not a value of this column.");
        }
        return Expression.Constant(converted, type);
    }

    private static string Text(JsonNode? v) => v switch
    {
        JsonValue x when x.GetValueKind() == JsonValueKind.String => x.GetValue<string>(),
        JsonValue x => x.ToJsonString(),
        _ => throw new ContractValidationException("This condition needs one value."),
    };

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
