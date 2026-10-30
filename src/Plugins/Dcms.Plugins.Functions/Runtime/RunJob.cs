using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.Functions.Runtime;

public sealed record RunPayload(string Function, JsonNode? Input, int Generation = 1);

/// <summary>
/// A function run later, as a tracked job (<c>jobs.enqueue</c>): it runs the instance's live
/// deployment when the job starts, not the one that enqueued it, so a rollback in between wins.
/// A failed run fails the job, which the ledger retries.
/// </summary>
public sealed class RunJob(FunctionsDbContext db, FunctionRunner runner, RunQueue runs) : IPluginJobHandler
{
    public const string Name = "run";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task<JobTicket> EnqueueAsync(IPluginContext context, Guid instanceId, string function, JsonNode? input, string? dedupeKey, int generation, CancellationToken ct) =>
        context.Contracts.Get<IPluginTrackedJobs>().EnqueueAsync(new TrackedJobEnqueue(
            Name,
            JsonSerializer.SerializeToElement(new RunPayload(function, input, generation), Json),
            InstanceId: instanceId,
            DedupeKey: dedupeKey is null ? null : $"functions-run:{instanceId}:{dedupeKey}",
            TitleKey: "jobs.kinds.functionsRun.title",
            TitleParams: new Dictionary<string, string> { ["function"] = function },
            RequiredPermission: "logs"), ct);

    public async Task RunAsync(JsonElement? payload, IPluginContext context, CancellationToken ct)
    {
        var run = payload?.Deserialize<RunPayload>(Json);
        if (run is null || context.Instance is not { } instance)
        {
            throw new ContractValidationException("A run needs its instance and function.");
        }
        FunctionDeployment? live;
        using (RlsScope.Environment(context.TenantId, context.EnvironmentId))
        {
            live = await db.Deployments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.InstanceId == instance.InstanceId && d.Status == FunctionDeploymentStatus.Active, ct);
        }
        var target = live is null ? null : RunTargets.Of(live, run.Function, live.VouchedPermissions);
        if (target is null)
        {
            throw new ContractConflictException($"No live deployment has a function '{run.Function}'.");
        }
        var outcome = await runner.RunAsync(target, run.Input, new RunTrigger("job", Generation: run.Generation), ct);
        using (RlsScope.Environment(context.TenantId, context.EnvironmentId))
        {
            await runs.RecordAsync(target, "job", run.Input, outcome, ct);
        }
        if (!outcome.Succeeded)
        {
            throw new InvalidOperationException($"{run.Function}: {outcome.Error}");
        }
    }
}
