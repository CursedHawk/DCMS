using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>The run history of one application, and what a person may do about a run: retry it, or start a flow.</summary>
public sealed class FlowRunService(AppsDbContext db, IPluginContext context, FlowRunQueue queue, IAuditRecorder audit, TimeProvider clock)
{
    private Guid InstanceId => context.Instance?.InstanceId
        ?? throw new InvalidOperationException("Flow runs are per instance; this context has none.");

    public static IReadOnlyList<FlowActionInfo> Actions() => ActionCatalog.All
        .Select(a => new FlowActionInfo(ActionCatalog.Key(a), a.Id, a.Major, a.Description,
            JsonNamingPolicy.CamelCase.ConvertName(a.Risk.ToString()), a.InputSchema))
        .ToList();

    public async Task<FlowRunPage> ListAsync(string? flow, string? status, int page, int pageSize, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 1);
        var instanceId = InstanceId;
        var runs = db.FlowRuns.AsNoTracking().Where(r => r.InstanceId == instanceId);
        if (!string.IsNullOrWhiteSpace(flow))
        {
            runs = runs.Where(r => r.FlowApiName == flow);
        }
        if (Enum.TryParse<FlowRunStatus>(status, ignoreCase: true, out var wanted))
        {
            runs = runs.Where(r => r.Status == wanted);
        }
        var total = await runs.CountAsync(ct);
        var items = await runs.OrderByDescending(r => r.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new FlowRunPage(items.Select(Summary).ToList(), total, page, pageSize);
    }

    public async Task<FlowRunDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var instanceId = InstanceId;
        var run = await db.FlowRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.InstanceId == instanceId, ct);
        if (run is null)
        {
            return null;
        }
        var steps = await db.FlowRunSteps.AsNoTracking().Where(s => s.RunId == id)
            .OrderBy(s => s.StartedAt).ThenBy(s => s.Attempt).ToListAsync(ct);
        var chain = await db.FlowRuns.AsNoTracking()
            .Where(r => r.InstanceId == instanceId && r.CorrelationId == run.CorrelationId)
            .OrderBy(r => r.CreatedAt).Take(100).ToListAsync(ct);
        return new FlowRunDetail(
            Summary(run),
            JsonNode.Parse(run.TriggerJson),
            steps.Select(s => new FlowRunStepInfo(s.StepId, s.Attempt, s.Action, Status(s.Status),
                s.InputJson is null ? null : JsonNode.Parse(s.InputJson),
                s.OutputJson is null ? null : JsonNode.Parse(s.OutputJson),
                s.Error, s.StartedAt, s.FinishedAt)).ToList(),
            chain.Select(Summary).ToList());
    }

    /// <summary>Queues a failed or terminated run again; the steps that succeeded are not repeated.</summary>
    /// <returns>False when there is no such run.</returns>
    /// <exception cref="ContractConflictException">The run has not ended in failure.</exception>
    public async Task<bool> RetryAsync(Guid id, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var instanceId = InstanceId;
        var run = await db.FlowRuns.FirstOrDefaultAsync(r => r.Id == id && r.InstanceId == instanceId, ct);
        if (run is null)
        {
            return false;
        }
        if (run.Status is not (FlowRunStatus.Failed or FlowRunStatus.Terminated))
        {
            throw new ContractConflictException($"Only a failed or terminated run can be retried; this one is {Status(run.Status)}.");
        }
        run.Status = FlowRunStatus.Pending;
        run.Attempts = 0;
        run.NextAttemptAt = clock.GetUtcNow();
        run.Error = null;
        run.FinishedAt = null;
        (audit.Declared ?? audit.Record($"plugin.{DynamicAppsPlugin.PluginId}.flow.retried"))
            .For("flow_run", run.Id, run.FlowApiName).With("instance", instanceId);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Starts a manual flow now, as the person asking.</summary>
    public async Task<Guid> StartAsync(string flow, JsonObject input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var runId = await queue.StartManualAsync(flow, input, EventOrigin.New(), Guid.NewGuid().ToString(), revision: null, ct);
        (audit.Declared ?? audit.Record($"plugin.{DynamicAppsPlugin.PluginId}.flow.started"))
            .For("flow_run", runId, flow).With("instance", InstanceId);
        return runId;
    }

    private static FlowRunSummary Summary(FlowRun r) => new(
        r.Id, r.FlowApiName, r.Revision, Status(r.Status),
        JsonNode.Parse(r.TriggerJson)?["eventName"]?.GetValue<string>() ?? "",
        r.Attempts, r.Depth, r.Writes, r.CorrelationId, r.CausationId, r.Error, r.CreatedAt, r.StartedAt, r.FinishedAt);

    private static string Status(FlowRunStatus status) => JsonNamingPolicy.CamelCase.ConvertName(status.ToString());
}
