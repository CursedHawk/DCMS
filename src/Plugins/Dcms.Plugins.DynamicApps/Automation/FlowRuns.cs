using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.DynamicApps;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>Which flows an event starts. Pure: the configuration it happened under, and the event.</summary>
public static class TriggerRouter
{
    public static IEnumerable<FlowDef> Match(AppConfig config, AppEvent evt)
    {
        foreach (var flow in config.Flows.Where(f => f.Enabled && f.Trigger.Event == evt.EventName))
        {
            var trigger = flow.Trigger;
            if (trigger.TableId is { } tableId
                && config.Tables.FirstOrDefault(t => t.Id == tableId)?.ApiName != evt.Entity?.Type)
            {
                continue;
            }
            if (trigger.RelationshipId is { } relationshipId
                && config.Relationships.FirstOrDefault(r => r.Id == relationshipId)?.ApiName != evt.Payload["relationship"]?.GetValue<string>())
            {
                continue;
            }
            if (trigger.ChangedFields.Count > 0)
            {
                var names = Members(config, trigger).Where(m => trigger.ChangedFields.Contains(m.Id)).Select(m => m.ApiName);
                if (!names.Intersect(evt.ChangedFields).Any())
                {
                    continue;
                }
            }
            yield return flow;
        }
    }

    private static IEnumerable<(Guid Id, string ApiName)> Members(AppConfig config, FlowTrigger trigger) =>
        (config.Tables.FirstOrDefault(t => t.Id == trigger.TableId)?.Fields.Select(f => (f.Id, f.ApiName)) ?? [])
        .Concat(config.Relationships.Where(r => r.SourceTableId == trigger.TableId).Select(r => (r.Id, r.ApiName)));

    /// <summary>The flow's definition hash: what a run is pinned to, alongside its revision.</summary>
    public static string Hash(FlowDef flow) =>
        ConfigJson.HashOf(ConfigJson.Sorted(JsonSerializer.SerializeToNode(flow, ConfigJson.Options))!.ToJsonString());
}

/// <summary>
/// Creates flow runs: from events, schedules, other flows and people. The database is the
/// queue; a run is a row the worker claims. Creation is idempotent per (event, flow, flow hash)
/// — a redelivered event or a retried schedule tick finds the run already there — and enforces
/// the cascade limits, recording a run it refuses as terminated so the history says why.
/// </summary>
public sealed class FlowRunQueue(AppsDbContext db, IPluginContext context, ConfigurationService configuration, TimeProvider clock)
{
    private Guid InstanceId => context.Instance?.InstanceId
        ?? throw new InvalidOperationException("Flow runs are per instance; this context has none.");

    /// <summary>Starts every flow the event triggers under the revision it happened in.</summary>
    public async Task<int> RouteAsync(AppEvent evt, CancellationToken ct)
    {
        if (await configuration.GetRevisionAsync(evt.Revision, ct) is not { } revision)
        {
            return 0;
        }
        var started = 0;
        foreach (var flow in TriggerRouter.Match(revision.Config, evt))
        {
            await EnqueueAsync(flow, evt.Revision, evt, ct);
            started++;
        }
        return started;
    }

    /// <summary>Runs a manual flow now: a person, the API or another flow (<c>flow.invoke</c>).</summary>
    /// <param name="idempotencyKey">Same key, same run: a retried <c>flow.invoke</c> step starts its flow once.</param>
    /// <exception cref="ContractValidationException">No enabled flow of that name has a manual trigger.</exception>
    public async Task<Guid> StartManualAsync(string flowName, JsonObject input, EventOrigin origin, string idempotencyKey,
        int? revision, CancellationToken ct)
    {
        var doc = revision is { } number ? await configuration.GetRevisionAsync(number, ct) : await configuration.GetPublishedAsync(ct);
        var flow = doc?.Config.Flows.FirstOrDefault(f => f.ApiName == flowName && f.Enabled)
                   ?? throw new ContractValidationException($"There is no enabled flow '{flowName}'.");
        if (flow.Trigger.Event != "manual")
        {
            throw new ContractValidationException($"Flow '{flowName}' has a {flow.Trigger.Event} trigger; only manual flows are started directly.");
        }
        var evt = Event(DeterministicId(idempotencyKey), "manual", doc!.Revision.Number, new JsonObject { ["input"] = input }, origin);
        return await EnqueueAsync(flow, doc.Revision.Number, evt, ct);
    }

    /// <summary>A schedule tick: one run per flow per due time, however many replicas see it.</summary>
    public async Task<Guid?> StartScheduledAsync(Guid flowId, DateTimeOffset due, CancellationToken ct)
    {
        var doc = await configuration.GetPublishedAsync(ct);
        if (doc?.Config.Flows.FirstOrDefault(f => f.Id == flowId && f.Enabled && f.Trigger.Event == "schedule") is not { } flow)
        {
            return null;
        }
        var evt = Event(DeterministicId($"{flowId}:{due.UtcTicks}"), "schedule", doc.Revision.Number,
            new JsonObject { ["due"] = due.UtcDateTime.ToString("O") }, EventOrigin.New());
        return await EnqueueAsync(flow, doc.Revision.Number, evt, ct);
    }

    private AppEvent Event(Guid id, string name, int revision, JsonObject payload, EventOrigin origin) => new()
    {
        EventId = id,
        TenantId = context.TenantId,
        SourceInstanceId = InstanceId,
        EventName = name,
        OccurredAt = clock.GetUtcNow(),
        Revision = revision,
        Payload = payload,
        CorrelationId = origin.CorrelationId,
        CausationId = origin.CausationId,
        Depth = origin.Depth,
    };

    private async Task<Guid> EnqueueAsync(FlowDef flow, int revision, AppEvent trigger, CancellationToken ct)
    {
        string? refused = null;
        if (trigger.Depth > AutomationLimits.MaxDepth)
        {
            refused = $"Not run: flows have already cascaded {trigger.Depth} levels deep (the limit is {AutomationLimits.MaxDepth}).";
        }
        else if (await db.FlowRuns.CountAsync(r => r.CorrelationId == trigger.CorrelationId, ct) >= AutomationLimits.MaxRunsPerCorrelation)
        {
            refused = $"Not run: one action has already set off {AutomationLimits.MaxRunsPerCorrelation} flow runs.";
        }

        var hash = TriggerRouter.Hash(flow);
        var now = clock.GetUtcNow();
        var status = (refused is null ? FlowRunStatus.Pending : FlowRunStatus.Terminated).ToString();
        var finished = refused is null ? (DateTimeOffset?)null : now;
        var trigger_ = JsonSerializer.Serialize(trigger, JsonSerializerOptions.Web);
        // ON CONFLICT is the idempotency: a redelivered event or a second replica's schedule tick
        // finds its run already there and creates nothing.
        var created = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO apps.flow_runs ("Id", "TenantId", "InstanceId", "FlowId", "FlowApiName", "Revision", "FlowHash", "TriggerEventId",
                "TriggerJson", "Status", "Attempts", "NextAttemptAt", "CorrelationId", "CausationId", "Depth", "Writes", "Error", "CreatedAt", "FinishedAt")
            VALUES ({Guid.NewGuid()}, {context.TenantId}, {InstanceId}, {flow.Id}, {flow.ApiName}, {revision}, {hash}, {trigger.EventId},
                {trigger_}::jsonb, {status}, 0, {now}, {trigger.CorrelationId}, {trigger.CausationId}, {trigger.Depth}, 0, {refused}, {now}, {finished})
            ON CONFLICT ("TenantId", "InstanceId", "TriggerEventId", "FlowId", "FlowHash") DO NOTHING
            RETURNING "Id" AS "Value"
            """).ToListAsync(ct);
        if (created.Count > 0)
        {
            return created[0];
        }
        var instanceId = InstanceId;
        return await db.FlowRuns
            .Where(r => r.InstanceId == instanceId && r.TriggerEventId == trigger.EventId && r.FlowId == flow.Id && r.FlowHash == hash)
            .Select(r => r.Id).FirstAsync(ct);
    }

    private static Guid DeterministicId(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
}
