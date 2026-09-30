using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.PluginSdk.Abstractions.Data;

/// <summary>
/// A table of a plugin's data that the admin console shows and edits on the plugin's page —
/// visitor accounts, form submissions, stored documents. The plugin describes the shape
/// (<see cref="DataSetSchema"/>) and answers queries; the console renders every data set with
/// the same table, filters and editor, so a plugin gets an admin screen without shipping UI.
///
/// <para>Constructed per request with the plugin's <see cref="IPluginContext"/> for the instance
/// being viewed (take it in the constructor), and dependencies from DI. Only what
/// <see cref="DataSetSchema"/> says it supports is ever called: the runtime refuses the rest
/// before reaching the plugin. Throw <see cref="ContractValidationException"/> for bad input
/// (400) and <see cref="ContractConflictException"/> for a state conflict (409).</para>
/// </summary>
public interface IPluginDataSet
{
    /// <summary>Columns, filters, actions and what may be changed. May depend on instance config.</summary>
    Task<DataSetSchema> DescribeAsync(CancellationToken ct);

    Task<DataPage> ListAsync(DataQuery query, CancellationToken ct);

    /// <summary>One row with every value the editor shows; null when it does not exist.</summary>
    Task<DataRow?> GetAsync(string key, CancellationToken ct);

    /// <summary>Values are already restricted to <see cref="DataSetSchema.ItemSchema"/>'s properties and validated against it.</summary>
    Task<DataRow> CreateAsync(JsonObject values, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>As <see cref="CreateAsync"/>; null when the row does not exist.</summary>
    Task<DataRow?> UpdateAsync(string key, JsonObject values, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>False when the row does not exist.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>Runs one of <see cref="DataSetSchema.Actions"/> on the selected rows.</summary>
    Task<DataActionResult> RunActionAsync(string action, IReadOnlyList<string> keys, JsonObject? input, CancellationToken ct)
        => throw new NotSupportedException();

    /// <summary>The row's file, for a data set of files; null when it does not exist.</summary>
    Task<DataFile?> DownloadAsync(string key, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>
/// Declares a data set in the manifest. <paramref name="ReadPermission"/> and
/// <paramref name="WritePermission"/> are full permission keys (<c>plugin:{id}:{action}</c>);
/// when null, the platform's plugin-management permission is required.
/// </summary>
public sealed record DataSetDeclaration(
    string Id,
    string Title,
    Type Implementation,
    string? Description = null,
    string? ReadPermission = null,
    string? WritePermission = null,
    string? IconName = null)
{
    public static DataSetDeclaration Of<TDataSet>(
        string id, string title, string? description = null,
        string? readPermission = null, string? writePermission = null, string? iconName = null)
        where TDataSet : IPluginDataSet
        => new(id, title, typeof(TDataSet), description, readPermission, writePermission, iconName);
}

/// <summary>What the console needs to render a data set.</summary>
/// <param name="ItemSchema">JSON Schema of the editable values; the create and edit forms are rendered from it.</param>
/// <param name="Searchable">Shows a search box; its text arrives as <see cref="DataQuery.Search"/>.</param>
/// <param name="DefaultSort">A sortable column's key.</param>
public sealed record DataSetSchema(
    IReadOnlyList<DataColumn> Columns,
    JsonObject? ItemSchema = null,
    IReadOnlyList<DataFilter>? Filters = null,
    IReadOnlyList<DataAction>? Actions = null,
    bool Searchable = false,
    bool CanCreate = false,
    bool CanUpdate = false,
    bool CanDelete = false,
    bool CanDownload = false,
    string? DefaultSort = null,
    bool DefaultDescending = false);

/// <param name="Kind">How the value renders: one of <see cref="DataColumnKinds"/>; the console shows unknown kinds as text.</param>
/// <param name="Primary">The row's heading on narrow screens and in the editor.</param>
public sealed record DataColumn(
    string Key,
    string Label,
    string Kind = DataColumnKinds.Text,
    bool Sortable = false,
    bool Primary = false);

public static class DataColumnKinds
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string DateTime = "datetime";
    public const string Email = "email";
    public const string Url = "url";

    /// <summary>A short status word shown as a badge.</summary>
    public const string Badge = "badge";

    /// <summary>A byte count, shown as KB/MB.</summary>
    public const string Bytes = "bytes";

    /// <summary>Any JSON value, shown compact.</summary>
    public const string Json = "json";

    /// <summary>A media asset id, shown as a thumbnail.</summary>
    public const string Media = "media";
}

/// <summary>A choice list above the table. The selected value arrives in <see cref="DataQuery.Filters"/>.</summary>
public sealed record DataFilter(string Key, string Label, IReadOnlyList<DataFilterOption> Options);

public sealed record DataFilterOption(string Value, string Label);

/// <summary>
/// Something done to selected rows beyond editing them: "Mark handled", "Sign out everywhere".
/// <paramref name="Risk"/> decides how hard the console asks before running it — a dangerous
/// action is confirmed by name.
/// </summary>
/// <param name="Bulk">May run on several selected rows at once; otherwise only from one row's editor.</param>
/// <param name="InputSchema">JSON Schema of a form the console shows before running it.</param>
public sealed record DataAction(
    string Id,
    string Label,
    OpRisk Risk = OpRisk.Safe,
    bool Bulk = true,
    string? Description = null,
    JsonObject? InputSchema = null);

/// <param name="Page">1-based.</param>
/// <param name="Filters">Selected filter values by <see cref="DataFilter.Key"/>; only declared keys arrive.</param>
public sealed record DataQuery(
    string? Search,
    string? Sort,
    bool Descending,
    IReadOnlyDictionary<string, string> Filters,
    int Page,
    int PageSize)
{
    public int Skip => (Page - 1) * PageSize;

    public string? Filter(string key) => Filters.TryGetValue(key, out var value) ? value : null;
}

public sealed record DataPage(IReadOnlyList<DataRow> Rows, long Total);

/// <param name="Key">Stable and unique in the data set: how the console addresses the row.</param>
/// <param name="Values">By column key, plus the <see cref="DataSetSchema.ItemSchema"/> properties when the row is fetched for editing.</param>
/// <param name="Title">The editor's heading; the primary column's value when null.</param>
public sealed record DataRow(string Key, JsonObject Values, string? Title = null);

public sealed record DataActionResult(int Affected, string? Message = null);

public sealed record DataFile(Stream Content, string ContentType, string FileName);
