using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>
/// Runs one claimed flow run to its end: the condition, then each step through the action
/// catalog, recording every attempt. The run executes the flow exactly as it was in the revision
/// it was created for. A retry resumes after the steps that already succeeded, and each side
/// effect carries the step's idempotency key, so delivery is at least once without doing a
/// thing twice. Runs inside the instance's plugin context (PluginHandlerRunner).
/// </summary>
public sealed class FlowExecutor(
    AppsDbContext db,
    IPluginContext context,
    IServiceProvider services,
    ConfigurationService configuration,
    AppEventLog events,
    IAuditRecorder audit,
    TimeProvider clock)
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    public async Task RunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.FlowRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.Status != FlowRunStatus.Running)
        {
            return;
        }
        using var activity = DcmsActivitySource.Start("dcms.dynamicapp.flow");
        activity?.SetTag("dcms.tenant", run.TenantId);
        activity?.SetTag("dcms.dynamicapp.instance", run.InstanceId);
        activity?.SetTag("dcms.dynamicapp.flow", run.FlowApiName);
        activity?.SetTag("dcms.dynamicapp.flow_version", run.Revision);
        activity?.SetTag("dcms.dynamicapp.flow_run", run.Id);

        var record = audit.Record($"plugin.{DynamicAppsPlugin.PluginId}.flow.executed")
            .For("flow_run", run.Id, run.FlowApiName)
            .With("instance", run.InstanceId)
            .With("revision", run.Revision)
            .With("attempt", run.Attempts)
            .With("correlation", run.CorrelationId);

        var (status, error, retry) = await ExecuteAsync(run, ct);

        var now = clock.GetUtcNow();
        if (status == FlowRunStatus.Failed && retry && run.Attempts < AutomationLimits.MaxAttempts)
        {
            // Retried later from the step that failed; the ones before it are done.
            status = FlowRunStatus.Pending;
            var next = now + Backoff[Math.Min(run.Attempts - 1, Backoff.Length - 1)];
            await Finish(run.Id, status, error, run.Writes, finished: null, next, ct);
        }
        else
        {
            await Finish(run.Id, status, error, run.Writes, finished: now, now, ct);
        }
        activity?.SetTag("dcms.dynamicapp.status", status.ToString());
        record.With("status", status.ToString()).With("writes", run.Writes);
        if (status is FlowRunStatus.Failed or FlowRunStatus.Terminated)
        {
            record.Failed(error);
        }
    }

    /// <summary>How the run ended, and whether a failure is worth another attempt.</summary>
    private readonly record struct Outcome(FlowRunStatus Status, string? Error = null, bool Retry = false);

    private async Task<Outcome> ExecuteAsync(FlowRun run, CancellationToken stopping)
    {
        if (run.Attempts > AutomationLimits.MaxAttempts)
        {
            return new(FlowRunStatus.Failed, "Abandoned: the run was claimed more times than it may be attempted.");
        }
        var doc = await configuration.GetRevisionAsync(run.Revision, stopping);
        var flow = doc?.Config.Flows.FirstOrDefault(f => f.Id == run.FlowId);
        if (flow is null || TriggerRouter.Hash(flow) != run.FlowHash)
        {
            return new(FlowRunStatus.Terminated, "The flow definition this run was created for no longer exists.");
        }

        var trigger = JsonSerializer.Deserialize<AppEvent>(run.TriggerJson, JsonSerializerOptions.Web)!;
        var outputs = new JsonObject();
        var scope = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["event"] = JsonSerializer.SerializeToNode(trigger, JsonSerializerOptions.Web),
            ["row"] = trigger.Payload["record"]?.DeepClone(),
            ["previous"] = trigger.Payload["previous"]?.DeepClone(),
            ["changedFields"] = new JsonArray(trigger.ChangedFields.Select(f => (JsonNode)f).ToArray()),
            ["input"] = trigger.Payload["input"]?.DeepClone(),
            ["steps"] = outputs,
            ["run"] = new JsonObject { ["id"] = run.Id.ToString(), ["flow"] = run.FlowApiName, ["revision"] = run.Revision, ["depth"] = run.Depth },
        };

        // What this run writes is caused by it, one level deeper.
        events.Origin = new EventOrigin(run.CorrelationId, run.Id, run.Depth + 1);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(AutomationLimits.MaxDuration);
        var ct = deadline.Token;
        try
        {
            var now = clock.GetUtcNow();
            if (!string.IsNullOrWhiteSpace(flow.Condition) && !Expressions.Truthy(Expressions.Evaluate(flow.Condition, scope, now)))
            {
                return new(FlowRunStatus.Skipped);
            }

            var done = await db.FlowRunSteps.AsNoTracking()
                .Where(s => s.RunId == run.Id && s.Status == FlowRunStatus.Succeeded)
                .ToDictionaryAsync(s => s.StepId, s => s.OutputJson, ct);

            foreach (var step in flow.Steps)
            {
                if (done.TryGetValue(step.Id, out var previousOutput))
                {
                    outputs[step.Id] = previousOutput is null ? null : JsonNode.Parse(previousOutput);
                    continue;
                }
                if (await StepAsync(run, step, scope, outputs, ct) is { } failure)
                {
                    return failure;
                }
            }
            return new(FlowRunStatus.Succeeded);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            return new(FlowRunStatus.Terminated, $"The run took longer than {AutomationLimits.MaxDuration.TotalSeconds:0} seconds.");
        }
        catch (ExpressionException e)
        {
            return new(FlowRunStatus.Failed, $"The flow's condition failed: {e.Message}");
        }
    }

    /// <returns>Null when the step succeeded or was skipped; otherwise how the run ends.</returns>
    private async Task<Outcome?> StepAsync(FlowRun run, FlowStep step, Dictionary<string, JsonNode?> scope, JsonObject outputs, CancellationToken ct)
    {
        using var activity = DcmsActivitySource.Start("dcms.dynamicapp.flow.step");
        activity?.SetTag("dcms.dynamicapp.flow_run", run.Id);
        activity?.SetTag("dcms.dynamicapp.step", step.Id);
        activity?.SetTag("dcms.dynamicapp.action", step.Action);

        // Counted per step rather than taken from the run: a run retried by hand starts its
        // attempts again, and its steps' history must not collide with the earlier ones.
        var attempt = await db.FlowRunSteps.CountAsync(s => s.RunId == run.Id && s.StepId == step.Id, ct) + 1;
        var row = new FlowRunStep
        {
            Id = Guid.NewGuid(),
            TenantId = run.TenantId,
            RunId = run.Id,
            StepId = step.Id,
            Attempt = attempt,
            Action = step.Action,
            Status = FlowRunStatus.Running,
            StartedAt = clock.GetUtcNow(),
        };
        try
        {
            var now = clock.GetUtcNow();
            if (!string.IsNullOrWhiteSpace(step.Condition) && !Expressions.Truthy(Expressions.Evaluate(step.Condition, scope, now)))
            {
                row.Status = FlowRunStatus.Skipped;
                outputs[step.Id] = null;
                await Save(row, ct);
                return null;
            }
            var action = ActionCatalog.Find(step.Action) ?? throw new FlowFatalException($"There is no action '{step.Action}'.");
            var input = Expressions.Render(step.Input, scope, now) as JsonObject ?? [];
            row.InputJson = Bounded(input);

            var output = await action.RunAsync(new FlowActionContext(services, context, run, step.Id), input, ct);
            row.Status = FlowRunStatus.Succeeded;
            row.OutputJson = Bounded(output);
            outputs[step.Id] = output?.DeepClone();
            await Save(row, ct);
            activity?.SetTag("dcms.dynamicapp.status", "succeeded");
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Whatever the failed action left half-tracked must not ride along with the next save.
            db.ChangeTracker.Clear();
            row.Status = FlowRunStatus.Failed;
            row.Error = Truncate(e.Message);
            await Save(row, ct);
            activity?.SetTag("dcms.dynamicapp.status", "failed");
            var error = $"Step '{step.Id}' ({step.Action}): {row.Error}";
            return e switch
            {
                FlowFatalException { Limit: true } => new Outcome(FlowRunStatus.Terminated, error),
                // The same input will fail the same way: bad input, a refused record, a conflict.
                FlowFatalException or ExpressionException or RecordValidationException
                    or ContractValidationException or ContractConflictException => new Outcome(FlowRunStatus.Failed, error),
                // Anything else may be passing trouble — the database, a contract's rate limit.
                _ => new Outcome(FlowRunStatus.Failed, error, Retry: true),
            };
        }
    }

    private async Task Save(FlowRunStep row, CancellationToken ct)
    {
        row.FinishedAt = clock.GetUtcNow();
        db.FlowRunSteps.Add(row);
        await db.SaveChangesAsync(ct);
        db.Entry(row).State = EntityState.Detached;
    }

    // Set-based, under the flow.executed entry RunAsync records.
    private Task Finish(Guid runId, FlowRunStatus status, string? error, int writes, DateTimeOffset? finished, DateTimeOffset next, CancellationToken ct) =>
        db.FlowRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, status)
            .SetProperty(r => r.Error, error == null ? null : Truncate(error))
            .SetProperty(r => r.Writes, writes)
            .SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(r => r.FinishedAt, finished)
            .SetProperty(r => r.NextAttemptAt, next), CancellationToken.None);

    /// <summary>Inputs and outputs are kept for the run inspector, up to a size worth keeping.</summary>
    private static string? Bounded(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }
        var json = node.ToJsonString();
        return json.Length <= 64_000 ? json : JsonSerializer.Serialize(new { truncated = true, chars = json.Length });
    }

    private static string Truncate(string text) => text.Length <= 3_900 ? text : text[..3_900] + "…";
}
