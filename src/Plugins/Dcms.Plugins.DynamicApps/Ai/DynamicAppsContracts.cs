using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Automation;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.DynamicApps;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.DynamicApps.Ai;

/// <summary>
/// <c>dynamic-apps.config@1</c> and <c>dynamic-apps.records@1</c>: the control and data planes
/// as contract operations, which the admin and AI agents call through the dispatcher (ADR 0021).
/// Every operation is the same service call the admin routes make — the assistant is one more
/// client of the same rules, never a second implementation — and the dispatcher has already
/// checked the caller's permission for it.
///
/// <para>The services are built here with this provider's context rather than resolved from DI:
/// a contract call can arrive on a route with no plugin instance of its own (the admin's
/// contract endpoint), or in-process from another plugin's request.</para>
/// </summary>
internal sealed class DynamicAppsContracts : IDynamicAppsConfig, IDynamicAppsRecords
{
    private readonly ConfigurationService _config;
    private readonly RecordService _records;
    private readonly FlowRunService _runs;
    private readonly RuntimeModelProvider _models;
    private readonly AiTrace? _ai;

    public DynamicAppsContracts(IServiceProvider services, IPluginContext context, IHttpContextAccessor http)
    {
        var events = ActivatorUtilities.CreateInstance<AppEventLog>(services, context);
        _config = ActivatorUtilities.CreateInstance<ConfigurationService>(services, context, events);
        _models = ActivatorUtilities.CreateInstance<RuntimeModelProvider>(services, context);
        _records = ActivatorUtilities.CreateInstance<RecordService>(services, context, _models, events);
        var queue = ActivatorUtilities.CreateInstance<FlowRunQueue>(services, context, _config);
        _runs = ActivatorUtilities.CreateInstance<FlowRunService>(services, context, queue);
        _ai = Trace(http.HttpContext);
    }

    /// <summary>
    /// An agent's call says so (<c>?plane=ai</c>) and which conversation, run and tool call it
    /// belongs to. Attribution only: what the call may do is the member's permissions, checked
    /// by the dispatcher before this runs.
    /// </summary>
    private static AiTrace? Trace(HttpContext? http)
    {
        if (http is null || !string.Equals(http.Request.Query["plane"], "ai", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        static Guid? Id(string? value) => Guid.TryParse(value, out var id) ? id : null;
        var toolCall = http.Request.Headers["X-Dcms-Ai-Tool-Call"].ToString();
        return new AiTrace(
            Id(http.Request.Headers["X-Dcms-Ai-Conversation"]),
            Id(http.Request.Headers["X-Dcms-Ai-Run"]),
            toolCall.Length is > 0 and <= 128 ? toolCall : null);
    }

    private AppChangeSource Source => _ai is null ? AppChangeSource.Human : AppChangeSource.Ai;

    // ------------------------------------------------------------------ configuration: reads

    public async Task<ConfigSummary> GetSummaryAsync(SummaryRequest input, CancellationToken ct)
    {
        var state = await _config.GetStateAsync(ct);
        var doc = await DocumentAsync(input.Revision, ct);
        var config = doc?.Config ?? new AppConfig();
        var tables = config.Tables.ToDictionary(t => t.Id);
        string TableName(Guid id) => tables.TryGetValue(id, out var t) ? t.ApiName : id.ToString();
        var choiceSets = config.ChoiceSets.ToDictionary(c => c.Id, c => c.ApiName);

        return new ConfigSummary(
            doc?.Revision,
            state.Published,
            state.Hash,
            config.Settings.Description,
            config.Tables.Select(t => new TableSummary(
                t.ApiName,
                t.DisplayName,
                t.Fields.FirstOrDefault(f => f.Id == t.PrimaryFieldId)?.ApiName,
                Access(t.Public),
                t.Fields.Select(f => Field(f, choiceSets)).ToList())).ToList(),
            config.Relationships.Select(r =>
                $"{TableName(r.SourceTableId)}.{r.ApiName} -> {TableName(r.TargetTableId)} ({Camel(r.Kind)}{(r.InverseApiName is { } i ? $", inverse {i}" : "")}{(r.Required ? ", required" : "")}, onDelete {Camel(r.OnDelete)})").ToList(),
            config.ChoiceSets.Select(c => $"{c.ApiName}: {string.Join(", ", c.Options.Select(o => o.Value))}").ToList(),
            config.Views.Select(v => $"{TableName(v.TableId)}.{v.ApiName}{(v.IsDefault ? " (default)" : "")}").ToList(),
            config.Flows.Select(f =>
                $"{f.ApiName}: {f.Trigger.Event}{(f.Trigger.TableId is { } t ? $" on {TableName(t)}" : "")}, {f.Steps.Count} step(s){(f.Enabled ? "" : ", disabled")}").ToList(),
            doc is null ? [] : await _config.IssuesAsync(config, state.Published is null ? null : (await _config.GetPublishedAsync(ct))?.Config, ct));
    }

    public async Task<TableDetail> GetTableAsync(TableRequest input, CancellationToken ct)
    {
        var config = (await DocumentAsync(input.Revision, ct))?.Config ?? new AppConfig();
        var table = config.Tables.FirstOrDefault(t => t.ApiName == input.Table)
                    ?? throw new ContractValidationException($"There is no table '{input.Table}'.");
        return new TableDetail(
            table,
            config.Relationships.Where(r => r.SourceTableId == table.Id || r.TargetTableId == table.Id).ToList(),
            config.Views.Where(v => v.TableId == table.Id).ToList());
    }

    public async Task<FlowDef> GetFlowAsync(FlowRequest input, CancellationToken ct) =>
        ((await DocumentAsync(input.Revision, ct))?.Config ?? new AppConfig()).Flows.FirstOrDefault(f => f.ApiName == input.Flow)
        ?? throw new ContractValidationException($"There is no flow '{input.Flow}'.");

    public Task<IReadOnlyList<FlowActionInfo>> ListActionsAsync(CancellationToken ct) => _runs.ActionsAsync(ct);

    public Task<RevisionPage> ListRevisionsAsync(CancellationToken ct) => _config.ListRevisionsAsync(1, 50, ct);

    public async Task<RevisionDiff> DiffRevisionsAsync(DiffRequest input, CancellationToken ct) =>
        await _config.DiffAsync(input.From, input.To, ct) ?? throw new ContractValidationException("Both revisions must exist.");

    public Task<RevisionPreview> PreviewDraftAsync(CancellationToken ct) => _config.PreviewAsync(ct);

    public async Task<JsonObject> GetPublicApiAsync(CancellationToken ct) =>
        await _models.GetAsync(ct) is { } model ? PublicApi.Model(model) : new JsonObject { ["tables"] = new JsonArray() };

    // ------------------------------------------------------------------ configuration: writes

    public Task<ApplyChangesResult> ApplyChangeSetAsync(ApplyChangesRequest input, CancellationToken ct) =>
        _config.ApplyAsync(input, Source, ct, _ai);

    public Task<ValidationResult> ValidateDraftAsync(CancellationToken ct) => _config.ValidateAsync(ct, _ai);

    public Task DiscardDraftAsync(ExpectedHashRequest input, CancellationToken ct) => _config.DiscardAsync(input.ExpectedHash, ct, _ai);

    public Task<PublishResult> PublishDraftAsync(ExpectedHashRequest input, CancellationToken ct) => _config.PublishAsync(input.ExpectedHash, ct, _ai);

    public Task<PublishResult> RollbackAsync(RollbackRequest input, CancellationToken ct) => _config.RollbackAsync(input, Source, ct, _ai);

    // ------------------------------------------------------------------ automation

    public Task<FlowRunPage> ListRunsAsync(RunQuery input, CancellationToken ct) => _runs.ListAsync(input.Flow, input.Status, input.Page, 20, ct);

    public async Task<FlowRunDetail> GetRunAsync(RunRequest input, CancellationToken ct) =>
        await _runs.GetAsync(input.Id, ct) ?? throw new ContractValidationException($"There is no run {input.Id}.");

    public async Task<StartFlowResult> StartFlowAsync(StartFlowCall input, CancellationToken ct) =>
        new(await _runs.StartAsync(input.Flow, input.Input ?? [], ct));

    public async Task RetryRunAsync(RunRequest input, CancellationToken ct)
    {
        if (!await _runs.RetryAsync(input.Id, ct))
        {
            throw new ContractValidationException($"There is no run {input.Id}.");
        }
    }

    // ------------------------------------------------------------------ records

    public Task<RecordPage> QueryRecordsAsync(TableQuery input, CancellationToken ct) =>
        Guarded(() => _records.QueryAsync(input.Table, input.Query ?? new RecordQuery(), RecordPlane.Admin, ct));

    public Task<JsonObject?> GetRecordAsync(RecordRequest input, CancellationToken ct) =>
        Guarded(() => _records.GetAsync(input.Table, input.Id, input.Expand ?? [], RecordPlane.Admin, ct));

    public Task<JsonObject> CreateRecordAsync(RecordCreate input, CancellationToken ct) =>
        Guarded(() => _records.CreateAsync(input.Table, input.Values, RecordPlane.Admin, ct));

    public async Task<JsonObject?> UpdateRecordAsync(RecordUpdate input, CancellationToken ct) =>
        await Guarded(() => _records.UpdateAsync(input.Table, input.Id, input.Values, RecordPlane.Admin, ct))
        ?? throw new ContractValidationException($"There is no {input.Table} record {input.Id}.");

    public async Task LinkRecordsAsync(RecordLink input, CancellationToken ct)
    {
        if (!await Guarded(() => _records.LinkAsync(input.Table, input.Id, input.Relationship, input.TargetId, ct)))
        {
            throw new ContractValidationException("Both records must exist.");
        }
    }

    public async Task UnlinkRecordsAsync(RecordLink input, CancellationToken ct) =>
        await Guarded(() => _records.UnlinkAsync(input.Table, input.Id, input.Relationship, input.TargetId, ct));

    public Task<BulkResult> BulkUpdateRecordsAsync(RecordsUpdate input, CancellationToken ct) =>
        Guarded(() => _records.BulkUpdateAsync(input.Table, new BulkUpdateRequest(input.Ids, input.Values), RecordPlane.Admin, ct));

    public async Task DeleteRecordAsync(RecordRequest input, CancellationToken ct)
    {
        if (!await Guarded(() => _records.DeleteAsync(input.Table, input.Id, null, RecordPlane.Admin, ct)))
        {
            throw new ContractValidationException($"There is no {input.Table} record {input.Id}.");
        }
    }

    public Task<BulkResult> BulkDeleteRecordsAsync(RecordsDelete input, CancellationToken ct) =>
        Guarded(() => _records.BulkDeleteAsync(input.Table, new BulkDeleteRequest(input.Ids), RecordPlane.Admin, ct));

    // ------------------------------------------------------------------ helpers

    /// <summary>A refused record answers like any refused contract call: 400 with every field's reason.</summary>
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

    private async Task<RevisionDocument?> DocumentAsync(string? which, CancellationToken ct) => which switch
    {
        "published" => await _config.GetPublishedAsync(ct),
        "draft" => await _config.GetDraftAsync(ct) ?? throw new ContractValidationException("There is no open draft."),
        _ => await _config.GetDraftAsync(ct) ?? await _config.GetPublishedAsync(ct),
    };

    private static string Field(FieldDef f, Dictionary<Guid, string> choiceSets)
    {
        var flags = new List<string>();
        if (f.Required) flags.Add("!");
        if (f.Unique) flags.Add("u");
        if (f.ReadOnly) flags.Add("ro");
        if (f.HiddenFromPublic) flags.Add("hidden");
        if (f.Deprecated) flags.Add("deprecated");
        var type = Camel(f.Type);
        if (f.ChoiceSetId is { } c && choiceSets.TryGetValue(c, out var set))
        {
            type += $"({set})";
        }
        return $"{f.ApiName}:{type}{(flags.Count > 0 ? " " + string.Join(" ", flags) : "")}";
    }

    private static string Access(PublicAccess access)
    {
        var parts = new List<string>();
        if (access.Read != PublicRead.None) parts.Add($"read:{Camel(access.Read)}");
        if (access.Create) parts.Add("create");
        if (access.UpdateOwn) parts.Add("updateOwn");
        if (access.DeleteOwn) parts.Add("deleteOwn");
        return string.Join(" ", parts);
    }

    private static string Camel<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
