using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.Functions.Build;
using Dcms.Shared.Data.Functions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.Functions.Runtime;

/// <summary>Where a run's chain began: the original action every run it sets off shares.</summary>
public sealed record RunOrigin(Guid CorrelationId, Guid? CausationId, int Depth)
{
    public static RunOrigin New() => new(Guid.NewGuid(), null, 0);
}

/// <summary>
/// Puts runs on the queue and keeps the history of the ones that ran at once (ADR 0029). The
/// database is the queue: a run is a row <see cref="RunWorker"/> claims. Queuing is idempotent per
/// (instance, trigger event, function), and enforces the cascade limits flows had, recording a run
/// it refuses as terminated so the history says why. Callers hold the run's environment scope.
/// </summary>
public sealed class RunQueue(FunctionsDbContext db)
{
    /// <summary>How many runs deep one original action may cascade.</summary>
    public const int MaxDepth = 5;

    /// <summary>How many runs one original action may set off in total, however they chain.</summary>
    public const int MaxRunsPerCorrelation = 20;

    public const int MaxAttempts = 3;
    public const int MaxResultChars = 64 * 1024;

    /// <summary>Waits before the second and third attempts.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    /// <returns>Whether a run was created (false: it already existed).</returns>
    public async Task<bool> EnqueueAsync(FunctionDeployment live, string function, string kind, Guid eventId, JsonNode? input,
        JsonNode? detail, RunOrigin origin, CancellationToken ct)
    {
        string? refused = null;
        if (origin.Depth > MaxDepth)
        {
            refused = $"Not run: runs have already cascaded {origin.Depth} levels deep (the limit is {MaxDepth}).";
        }
        else if (origin.Depth > 0 && await db.Runs.CountAsync(r => r.CorrelationId == origin.CorrelationId, ct) >= MaxRunsPerCorrelation)
        {
            refused = $"Not run: one action has already set off {MaxRunsPerCorrelation} runs.";
        }
        var now = DateTimeOffset.UtcNow;
        var status = (refused is null ? FunctionRunStatus.Pending : FunctionRunStatus.Terminated).ToString();
        var finished = refused is null ? (DateTimeOffset?)null : now;
        var inputJson = input?.ToJsonString();
        var detailJson = detail?.ToJsonString();
        // ON CONFLICT is the idempotency: a redelivered event or another replica's schedule tick
        // finds its run already there and creates nothing.
        var created = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO functions.runs ("Id", "TenantId", "EnvironmentId", "InstanceId", "Function", "DeploymentId", "DeploymentNumber",
                "TriggerKind", "TriggerEventId", "Input", "TriggerDetail", "Status", "Attempts", "NextAttemptAt", "CorrelationId", "CausationId",
                "Depth", "Error", "Calls", "DurationMs", "CreatedAt", "FinishedAt")
            VALUES ({Guid.CreateVersion7()}, {live.TenantId}, {live.EnvironmentId}, {live.InstanceId}, {function}, {live.Id}, {live.Number},
                {kind}, {eventId}, {inputJson}::jsonb, {detailJson}::jsonb, {status}, 0, {now}, {origin.CorrelationId}, {origin.CausationId},
                {origin.Depth}, {refused}, 0, 0, {now}, {finished})
            ON CONFLICT ("TenantId", "EnvironmentId", "InstanceId", "TriggerEventId", "Function") DO NOTHING
            RETURNING "Id" AS "Value"
            """).ToListAsync(ct);
        return created.Count > 0 && refused is null;
    }

    /// <summary>Records a run that ran at once (manual, job, HTTP), for the history.</summary>
    public async Task RecordAsync(RunTarget target, string kind, JsonNode? input, RunOutcome outcome, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        db.Runs.Add(Finish(new FunctionRun
        {
            Id = Guid.CreateVersion7(),
            TenantId = target.Deployment.TenantId,
            EnvironmentId = target.Deployment.EnvironmentId,
            InstanceId = target.Deployment.InstanceId,
            Function = target.Function.Name,
            DeploymentId = target.Deployment.Id,
            DeploymentNumber = target.Deployment.Number,
            TriggerKind = kind,
            TriggerEventId = Guid.CreateVersion7(),
            Input = Cap(input?.ToJsonString()),
            Attempts = 1,
            CorrelationId = Guid.NewGuid(),
            CreatedAt = now.AddMilliseconds(-outcome.DurationMs),
            StartedAt = now.AddMilliseconds(-outcome.DurationMs),
        }, outcome, retry: false));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Writes what a run produced onto its row: succeeded, failed for good, or pending its next attempt.</summary>
    public static FunctionRun Finish(FunctionRun run, RunOutcome outcome, bool retry)
    {
        run.Result = outcome.Succeeded ? Cap(outcome.Result?.ToJsonString() ?? "null") : null;
        run.Error = outcome.Error is { Length: > 4000 } e ? e[..4000] : outcome.Error;
        run.Logs = outcome.Logs;
        run.Calls = outcome.Calls;
        run.DurationMs = outcome.DurationMs;
        run.LeaseUntil = null;
        if (outcome.Succeeded || !retry)
        {
            run.Status = outcome.Succeeded ? FunctionRunStatus.Succeeded : FunctionRunStatus.Failed;
            run.FinishedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            run.Status = FunctionRunStatus.Pending;
            run.NextAttemptAt = DateTimeOffset.UtcNow + Backoff[Math.Min(run.Attempts, Backoff.Length) - 1];
        }
        return run;
    }

    /// <summary>The live deployments of this environment that have a function on a trigger, and those functions.</summary>
    public async Task<List<(FunctionDeployment Live, List<FunctionSpec> Functions)>> OnAsync(Func<JsonObject, bool> trigger, CancellationToken ct)
    {
        var live = await Live(db.Deployments.Where(d => d.Status == FunctionDeploymentStatus.Active)).ToListAsync(ct);
        return live
            .Select(d => (d, FunctionManifest.Deserialize(d.Manifest)
                .Where(f => f.Triggers.Any(t => trigger(t) && FunctionManifest.MayReceive(t, d.VouchedPermissions))).ToList()))
            .Where(x => x.Item2.Count > 0)
            .ToList();
    }

    /// <summary>Deployments without their bundles: what queuing needs, a fraction of the bytes.</summary>
    public static IQueryable<FunctionDeployment> Live(IQueryable<FunctionDeployment> deployments) =>
        deployments.AsNoTracking().Select(d => new FunctionDeployment
        {
            Id = d.Id, TenantId = d.TenantId, EnvironmentId = d.EnvironmentId, InstanceId = d.InstanceId,
            Number = d.Number, Status = d.Status, Manifest = d.Manifest, VouchedPermissions = d.VouchedPermissions,
        });

    /// <summary>The same key, the same id: what makes a redelivery or a second tick find its run.</summary>
    public static Guid EventId(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));

    private static string? Cap(string? json) =>
        json is { Length: > MaxResultChars } ? JsonSerializer.Serialize(new { truncated = true, chars = json.Length }) : json;
}
