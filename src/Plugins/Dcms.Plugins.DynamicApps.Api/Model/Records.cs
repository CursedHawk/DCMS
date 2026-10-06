using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dcms.Plugins.DynamicApps.Api.Model;

/// <summary>
/// A query over one table's records, validated against the published model and compiled to
/// parameterized SQL — never SQL itself. Field names are api names; a lookup's fields are
/// reached with one hop (<c>company.name</c>). System fields: <c>id</c>, <c>created_at</c>,
/// <c>updated_at</c>, <c>created_by</c>.
/// </summary>
public sealed record RecordQuery
{
    /// <summary>The fields to return; all of them when empty. <c>id</c> and <c>version</c> always come back.</summary>
    public IReadOnlyList<string> Select { get; init; } = [];

    public RecordFilter? Filter { get; init; }

    public IReadOnlyList<RecordSort> Sort { get; init; } = [];

    /// <summary>Free text matched against the table's searchable fields (its primary field when none is).</summary>
    public string? Search { get; init; }

    /// <summary>Lookups to return as the record they point at instead of its id.</summary>
    public IReadOnlyList<string> Expand { get; init; } = [];

    /// <summary>1-based.</summary>
    public int Page { get; init; } = 1;

    /// <summary>At most 200.</summary>
    public int PageSize { get; init; } = 50;
}

/// <summary>
/// One node of a filter: exactly one of <see cref="And"/>, <see cref="Or"/>, <see cref="Not"/>,
/// or a comparison (<see cref="Field"/> + <see cref="Op"/> + <see cref="Value"/>).
/// </summary>
/// <remarks>
/// Operators: <c>eq ne gt gte lt lte in nin contains startsWith isNull</c>. Which apply depends
/// on the field's type; <c>in</c>/<c>nin</c> take a list, <c>isNull</c> takes true or false.
/// </remarks>
public sealed record RecordFilter
{
    public IReadOnlyList<RecordFilter>? And { get; init; }
    public IReadOnlyList<RecordFilter>? Or { get; init; }
    public RecordFilter? Not { get; init; }
    public string? Field { get; init; }
    public string? Op { get; init; }
    public JsonNode? Value { get; init; }
}

[JsonConverter(typeof(CamelCaseEnumConverter<SortDirection>))]
public enum SortDirection
{
    Asc,
    Desc,
}

public sealed record RecordSort(string Field, SortDirection Direction = SortDirection.Asc);

/// <summary>
/// A page of records. Each record is an object of api names to values, plus <c>id</c>,
/// <c>version</c>, <c>created_at</c>, <c>updated_at</c> and <c>created_by</c>.
/// </summary>
public sealed record RecordPage(IReadOnlyList<JsonObject> Items, int Total, int Page, int PageSize);

public sealed record BulkUpdateRequest(IReadOnlyList<Guid> Ids, JsonObject Values);

public sealed record BulkDeleteRequest(IReadOnlyList<Guid> Ids);

/// <param name="Affected">Records changed; the rest did not exist.</param>
public sealed record BulkResult(int Affected);

public sealed record LinkRequest(Guid TargetId);
