using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.PluginSdk.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.DynamicApps.Ai;

/// <summary>
/// <c>dynamic-apps.automation@1</c>: the same record service as the admin's contract, on the
/// system plane flows use. Built with this provider's context, as <see cref="DynamicAppsContracts"/> is.
/// </summary>
internal sealed class AutomationRecords : IDynamicAppsAutomation
{
    private readonly RecordService _records;
    private readonly ConfigurationService _config;

    public AutomationRecords(IServiceProvider services, IPluginContext context)
    {
        var events = ActivatorUtilities.CreateInstance<AppEventLog>(services, context);
        var models = ActivatorUtilities.CreateInstance<RuntimeModelProvider>(services, context);
        _records = ActivatorUtilities.CreateInstance<RecordService>(services, context, models, events);
        _config = ActivatorUtilities.CreateInstance<ConfigurationService>(services, context, events);
    }

    public async Task<AppModel?> ModelAsync(CancellationToken ct) =>
        await _config.GetPublishedAsync(ct) is { Config: var c }
            ? new AppModel(c.Tables.Where(t => t.Enabled).ToList(), c.Relationships, c.ChoiceSets)
            : null;

    public Task<RecordPage> QueryAsync(TableQuery input, CancellationToken ct) =>
        Guarded(() => _records.QueryAsync(input.Table, input.Query ?? new RecordQuery(), RecordPlane.System, ct));

    public Task<JsonObject?> GetAsync(RecordRequest input, CancellationToken ct) =>
        Guarded(() => _records.GetAsync(input.Table, input.Id, input.Expand ?? [], RecordPlane.System, ct));

    public Task<JsonObject> CreateAsync(RecordCreate input, CancellationToken ct) =>
        Guarded(() => _records.CreateAsync(input.Table, input.Values, RecordPlane.System, ct));

    public Task<JsonObject?> UpdateAsync(RecordUpdate input, CancellationToken ct) =>
        Guarded(() => _records.UpdateAsync(input.Table, input.Id, input.Values, RecordPlane.System, ct));

    public Task<bool> DeleteAsync(RecordRequest input, CancellationToken ct) =>
        Guarded(() => _records.DeleteAsync(input.Table, input.Id, null, RecordPlane.System, ct));

    public Task<bool> LinkAsync(RecordLink input, CancellationToken ct) =>
        Guarded(() => _records.LinkAsync(input.Table, input.Id, input.Relationship, input.TargetId, ct));

    public Task<bool> UnlinkAsync(RecordLink input, CancellationToken ct) =>
        Guarded(() => _records.UnlinkAsync(input.Table, input.Id, input.Relationship, input.TargetId, ct));

    public Task<BulkResult> BulkUpdateAsync(RecordsUpdate input, CancellationToken ct) =>
        Guarded(() => _records.BulkUpdateAsync(input.Table, new BulkUpdateRequest(input.Ids, input.Values), RecordPlane.System, ct));

    public Task<BulkResult> BulkDeleteAsync(RecordsDelete input, CancellationToken ct) =>
        Guarded(() => _records.BulkDeleteAsync(input.Table, new BulkDeleteRequest(input.Ids), RecordPlane.System, ct));

    private static async Task<T> Guarded<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (RecordValidationException e)
        {
            throw new ContractValidationException(e.Message);
        }
    }
}
