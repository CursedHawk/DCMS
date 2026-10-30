using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.Shared.Audit;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.Functions.Runtime;

/// <summary>
/// Functions' background work, in jobs-worker (ADR 0029): starts due schedules, and runs queued
/// runs in a few slots. The database is the queue: a run is claimed with <c>FOR UPDATE SKIP
/// LOCKED</c> and a lease, so replicas share the work and a crashed one's runs come back when
/// the lease ends. No tenant holds more than <see cref="PerTenant"/> slots at once, so one
/// tenant's backlog cannot starve the rest. Cross-tenant claims run under
/// <see cref="RlsScope.Platform"/>; each run under its own tenant and environment.
/// </summary>
public sealed partial class RunWorker(
    IServiceProvider services,
    PluginHandlerRunner handlers,
    ILogger<RunWorker> logger) : BackgroundService
{
    public const int Slots = 4;
    public const int PerTenant = 2;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ScheduleEvery = TimeSpan.FromSeconds(30);

    /// <summary>Longer than any run may take (5 min) with the runner's own margin.</summary>
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(7);

    private DateTimeOffset _nextSchedule = DateTimeOffset.UtcNow;
    private DateTimeOffset _nextCleanup = DateTimeOffset.UtcNow.AddMinutes(5);

    // Only this class's own constants are formatted into the statement.
    private static readonly string ClaimSql = $"""
        UPDATE functions.runs r
        SET "Status" = 'Running', "Attempts" = r."Attempts" + 1, "LeaseUntil" = now() + make_interval(secs => {(int)Lease.TotalSeconds}),
            "StartedAt" = COALESCE(r."StartedAt", now())
        WHERE r."Id" = (
            SELECT c."Id" FROM functions.runs c
            WHERE ((c."Status" = 'Pending' AND c."NextAttemptAt" <= now()) OR (c."Status" = 'Running' AND c."LeaseUntil" < now()))
              AND (SELECT count(*) FROM functions.runs x
                   WHERE x."TenantId" = c."TenantId" AND x."Status" = 'Running' AND x."LeaseUntil" >= now()) < {PerTenant}
            ORDER BY c."NextAttemptAt" FOR UPDATE SKIP LOCKED LIMIT 1)
        RETURNING r."Id", r."TenantId", r."EnvironmentId", r."InstanceId"
        """;

    private sealed class Claimed
    {
        public Guid Id { get; init; }
        public Guid TenantId { get; init; }
        public Guid EnvironmentId { get; init; }
        public Guid InstanceId { get; init; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new List<Task>(Slots);
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                if (DateTimeOffset.UtcNow >= _nextSchedule)
                {
                    _nextSchedule = DateTimeOffset.UtcNow + ScheduleEvery;
                    await ScheduleAsync(DateTimeOffset.UtcNow, stoppingToken);
                }
                if (DateTimeOffset.UtcNow >= _nextCleanup)
                {
                    _nextCleanup = DateTimeOffset.UtcNow.AddHours(1);
                    await CleanupAsync(stoppingToken);
                }
                // Fill every free slot; a slot frees the moment its run ends.
                running.RemoveAll(t => t.IsCompleted);
                while (running.Count < Slots && await ClaimAsync(stoppingToken) is { } run)
                {
                    running.Add(ExecuteAsync(run, stoppingToken));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Functions worker pass failed; retrying.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
        await Task.WhenAll(running).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task<Claimed?> ClaimAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        using var platform = RlsScope.Platform();
        var db = scope.ServiceProvider.GetRequiredService<FunctionsDbContext>();
        return (await db.Database.SqlQueryRaw<Claimed>(ClaimSql).ToListAsync(ct)).FirstOrDefault();
    }

    /// <summary>Runs one claimed run in its tenant and environment, and writes what came of it.</summary>
    public async Task ExecuteAsync(Guid runId, Guid tenantId, Guid environmentId, Guid instanceId, CancellationToken ct) =>
        await ExecuteAsync(new Claimed { Id = runId, TenantId = tenantId, EnvironmentId = environmentId, InstanceId = instanceId }, ct);

    private async Task ExecuteAsync(Claimed claimed, CancellationToken ct)
    {
        try
        {
            using var environment = RlsScope.Environment(claimed.TenantId, claimed.EnvironmentId);
            var ran = await handlers.RunAsync(claimed.TenantId, FunctionsPlugin.PluginId, claimed.InstanceId, async (_, sp) =>
            {
                var db = sp.GetRequiredService<FunctionsDbContext>();
                var run = await db.Runs.FirstAsync(r => r.Id == claimed.Id, ct);
                var deployment = await db.Deployments.AsNoTracking().FirstAsync(d => d.Id == run.DeploymentId, ct);
                var target = RunTargets.Of(deployment, run.Function, deployment.VouchedPermissions);
                RunOutcome outcome;
                if (target is null)
                {
                    outcome = new RunOutcome("error", null, $"Deployment {deployment.Number} has no function '{run.Function}'.", "", 0, 0);
                }
                else if (run.Attempts > RunQueue.MaxAttempts)
                {
                    // Its lease ran out on the last attempt: the worker holding it stopped.
                    outcome = new RunOutcome("error", null, "The run was interrupted on its last attempt.", run.Logs ?? "", 0, 0);
                }
                else
                {
                    outcome = await sp.GetRequiredService<FunctionRunner>().RunAsync(target, Parse(run.Input),
                        new RunTrigger(run.TriggerKind, Parse(run.TriggerDetail), Origin: new RunOrigin(run.CorrelationId, run.CausationId, run.Depth), RunId: run.Id),
                        ct);
                }
                // A function that threw is tried again; one that hit a limit would only hit it again.
                RunQueue.Finish(run, outcome, retry: outcome.Status == "error" && target is not null && run.Attempts < RunQueue.MaxAttempts);
                sp.GetRequiredService<IAuditRecorder>().Record("plugin.functions.run.executed")
                    .For("function", run.Id, run.Function)
                    .With("trigger", run.TriggerKind).With("status", run.Status.ToString()).With("attempt", run.Attempts)
                    .With("deployment", run.DeploymentNumber);
                await db.SaveChangesAsync(ct);
            }, ct);
            if (!ran)
            {
                await EndAsync(claimed, "The functions app was switched off before the run started.");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Reaching here is the worker failing, not the function: the lease ends and another
            // pass picks the run up, within its attempts.
            logger.LogWarning(ex, "Function run {RunId} could not be executed.", claimed.Id);
        }
    }

    private async Task EndAsync(Claimed run, string error)
    {
        using var scope = services.CreateScope();
        using var rls = RlsScope.Environment(run.TenantId, run.EnvironmentId);
        var db = scope.ServiceProvider.GetRequiredService<FunctionsDbContext>();
        await db.Runs.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, FunctionRunStatus.Terminated)
            .SetProperty(r => r.Error, error)
            .SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(r => r.FinishedAt, DateTimeOffset.UtcNow));
    }

    // ------------------------------------------------------------------ schedules

    /// <summary>
    /// Queues every schedule due at <paramref name="now"/>: a cron's current minute when it
    /// matches, an interval's current slot. The slot is the run's event id, so every replica and
    /// every pass within it start it once. A slot missed while no worker ran is not caught up.
    /// </summary>
    public async Task<int> ScheduleAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<FunctionDeployment> live;
        using (var scope = services.CreateScope())
        using (RlsScope.Platform())
        {
            var db = scope.ServiceProvider.GetRequiredService<FunctionsDbContext>();
            // rls: RlsScope.Platform, every tenant's live deployments with a schedule.
            live = await RunQueue.Live(db.Deployments.IgnoreTenantFilter()
                    .Where(d => d.Status == FunctionDeploymentStatus.Active)
                    .Where(d => EF.Functions.JsonContains(d.Manifest, """{"functions":[{"triggers":[{"kind":"schedule"}]}]}""")
                                || EF.Functions.JsonContains(d.Manifest, """{"functions":[{"triggers":[{"kind":"every"}]}]}""")))
                .ToListAsync(ct);
        }
        var minute = new DateTime(now.UtcDateTime.Ticks - now.UtcDateTime.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var started = 0;
        foreach (var deployment in live)
        {
            foreach (var function in Build.FunctionManifest.Deserialize(deployment.Manifest))
            {
                foreach (var (trigger, index) in function.Triggers.Select((t, i) => (t, i)))
                {
                    if (Slot(trigger, minute) is not { } slot) continue;
                    try
                    {
                        using var scope = services.CreateScope();
                        using var rls = RlsScope.Environment(deployment.TenantId, deployment.EnvironmentId);
                        var queue = new RunQueue(scope.ServiceProvider.GetRequiredService<FunctionsDbContext>());
                        if (await queue.EnqueueAsync(deployment, function.Name, "schedule",
                                RunQueue.EventId($"schedule:{deployment.InstanceId:N}:{function.Name}:{index}:{slot.Ticks}"),
                                new JsonObject { ["due"] = slot.ToString("O") }, (JsonObject)trigger.DeepClone(), RunOrigin.New(), ct))
                        {
                            started++;
                        }
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Could not start scheduled function {Function}.", function.Name);
                    }
                }
            }
        }
        return started;
    }

    /// <summary>The slot <paramref name="minute"/> falls in for a schedule trigger; null when it is not one, or not due.</summary>
    public static DateTime? Slot(JsonObject trigger, DateTime minute)
    {
        switch (trigger["kind"]?.GetValue<string>())
        {
            case "schedule":
                return Cron.TryParse(trigger["cron"]?.GetValue<string>() ?? "") is { } cron && cron.Matches(minute) ? minute : null;
            case "every":
                var match = Interval().Match(trigger["interval"]?.GetValue<string>() ?? "");
                if (!match.Success) return null;
                var every = TimeSpan.FromMinutes(int.Parse(match.Groups[1].Value) * (match.Groups[2].Value switch { "m" => 1, "h" => 60, _ => 1_440 }));
                return new DateTime(minute.Ticks - minute.Ticks % every.Ticks, DateTimeKind.Utc);
            default:
                return null;
        }
    }

    [GeneratedRegex(@"^(\d{1,4})(m|h|d)$")]
    private static partial Regex Interval();

    // ------------------------------------------------------------------ retention

    /// <summary>Finished runs after thirty days.</summary>
    private async Task CleanupAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        using var rls = RlsScope.Platform();
        var db = scope.ServiceProvider.GetRequiredService<FunctionsDbContext>();
        var before = DateTimeOffset.UtcNow.AddDays(-30);
        // rls: RlsScope.Platform, every tenant's finished runs.
        var deleted = await db.Runs.IgnoreTenantFilter().Where(r => r.FinishedAt < before).ExecuteDeleteAsync(ct);
        if (deleted > 0)
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();
            audit.Record($"plugin.{FunctionsPlugin.PluginId}.retention").With("runs", deleted);
            await audit.FlushAsync(ct);
        }
    }

    private static JsonNode? Parse(string? json) => json is null ? null : JsonNode.Parse(json);
}
