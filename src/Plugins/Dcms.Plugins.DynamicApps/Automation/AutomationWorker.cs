using System.Text.Json;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>
/// The automation engine's one background loop, in admin-api (ADR 0021). Each tick it:
/// routes outbox events to the flows they trigger, starts due schedules, and claims runnable
/// runs and executes them. The database is the queue throughout: rows are claimed with
/// <c>FOR UPDATE SKIP LOCKED</c> (runs additionally with a lease), so any number of replicas
/// share the work and a crashed one's runs are picked up when its lease ends.
///
/// <para>Each tenant's work runs through <see cref="PluginHandlerRunner"/>: its tenant scope,
/// the check that the instance is still enabled, its plugin context and its audit flush. The
/// cross-tenant claims run under <see cref="RlsScope.Platform"/>, the explicit widening.</para>
/// </summary>
public sealed class AutomationWorker(
    IServiceProvider services,
    PluginHandlerRunner runner,
    AuditAmbient ambient,
    ILogger<AutomationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private const int OutboxBatch = 100;
    private const int MaxOutboxAttempts = 10;
    private const int ScheduleBatch = 50;
    private const int Concurrency = 4;
    private static readonly TimeSpan Lease = AutomationLimits.MaxDuration * 2;

    private DateTimeOffset _nextCleanup = DateTimeOffset.UtcNow.AddMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                await RouteAsync(stoppingToken);
                await ScheduleAsync(stoppingToken);
                await ExecuteRunsAsync(stoppingToken);
                if (DateTimeOffset.UtcNow >= _nextCleanup)
                {
                    await CleanupAsync(stoppingToken);
                    _nextCleanup = DateTimeOffset.UtcNow.AddHours(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Dynamic Apps automation pass failed; retrying.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // ------------------------------------------------------------------ outbox → runs

    private const string ClaimOutboxSql = """
        SELECT * FROM apps.outbox
        WHERE "SentAt" IS NULL
        ORDER BY "OccurredAt"
        FOR UPDATE SKIP LOCKED
        LIMIT 100
        """;

    /// <summary>Hands each pending event to its instance's router. Routing is idempotent, so a crash before the stamp only repeats work.</summary>
    public async Task<int> RouteAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppsDbContext>();
        // rls: none needed. apps.outbox is exempt (RlsConfigurator.ExemptTables): drained across
        // tenants, each row carrying its own.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pending = await db.Outbox.FromSqlRaw(ClaimOutboxSql).ToListAsync(ct);
        foreach (var message in pending)
        {
            var restored = new AuditScope();
            AuditPropagation.Restore(restored, AuditPropagation.FromJson(message.ContextJson), message.TenantId);
            using (ambient.Enter(restored))
            {
                try
                {
                    var evt = JsonSerializer.Deserialize<AppEvent>(message.Envelope, JsonSerializerOptions.Web)!;
                    // False: the instance was disabled or deleted; its events have nowhere to go.
                    await runner.RunAsync(message.TenantId, DynamicAppsPlugin.PluginId, message.InstanceId,
                        (_, sp) => sp.GetRequiredService<FlowRunQueue>().RouteAsync(evt, ct), ct);
                    message.SentAt = DateTimeOffset.UtcNow;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    message.Attempts++;
                    message.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                    if (message.Attempts >= MaxOutboxAttempts)
                    {
                        logger.LogError(ex, "Giving up routing Dynamic Apps event {EventId} after {Attempts} attempts.", message.Id, message.Attempts);
                        message.SentAt = DateTimeOffset.UtcNow;
                    }
                }
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return pending.Count;
    }

    // ------------------------------------------------------------------ schedules → runs

    private sealed class DueSchedule
    {
        public Guid TenantId { get; init; }
        public Guid InstanceId { get; init; }
        public Guid FlowId { get; init; }
        public DateTimeOffset Due { get; init; }
    }

    // Only this class's own integers are formatted into these statements.
    private static readonly string ClaimSchedulesSql = $"""
        WITH due AS (
            SELECT "Id", "NextRunAt" FROM apps.flow_schedules
            WHERE "NextRunAt" <= now() ORDER BY "NextRunAt" FOR UPDATE SKIP LOCKED LIMIT {ScheduleBatch})
        UPDATE apps.flow_schedules s
        SET "NextRunAt" = GREATEST(s."NextRunAt" + make_interval(mins => s."EveryMinutes"), now())
        FROM due WHERE s."Id" = due."Id"
        RETURNING s."TenantId", s."InstanceId", s."FlowId", due."NextRunAt" AS "Due"
        """;

    private static readonly string ClaimRunsSql = $"""
        UPDATE apps.flow_runs r
        SET "Status" = 'Running', "Attempts" = r."Attempts" + 1, "LeaseUntil" = now() + make_interval(secs => {(int)Lease.TotalSeconds}),
            "StartedAt" = COALESCE(r."StartedAt", now())
        WHERE r."Id" IN (
            SELECT "Id" FROM apps.flow_runs
            WHERE ("Status" = 'Pending' AND "NextAttemptAt" <= now()) OR ("Status" = 'Running' AND "LeaseUntil" < now())
            ORDER BY "NextAttemptAt" FOR UPDATE SKIP LOCKED LIMIT {Concurrency})
        RETURNING r."Id", r."TenantId", r."InstanceId"
        """;

    /// <summary>Starts every schedule that is due and moves it to its next time, missed ticks collapsed into one.</summary>
    public async Task<int> ScheduleAsync(CancellationToken ct)
    {
        List<DueSchedule> due;
        using (var scope = services.CreateScope())
        using (RlsScope.Platform())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppsDbContext>();
            due = await db.Database.SqlQueryRaw<DueSchedule>(ClaimSchedulesSql).ToListAsync(ct);
        }
        foreach (var schedule in due)
        {
            try
            {
                await runner.RunAsync(schedule.TenantId, DynamicAppsPlugin.PluginId, schedule.InstanceId,
                    (_, sp) => sp.GetRequiredService<FlowRunQueue>().StartScheduledAsync(schedule.FlowId, schedule.Due, ct), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not start scheduled flow {FlowId}.", schedule.FlowId);
            }
        }
        return due.Count;
    }

    // ------------------------------------------------------------------ runs

    private sealed class ClaimedRun
    {
        public Guid Id { get; init; }
        public Guid TenantId { get; init; }
        public Guid InstanceId { get; init; }
    }

    /// <summary>Claims up to <see cref="Concurrency"/> runnable runs — due pending ones, and running ones whose lease ran out — and runs them.</summary>
    public async Task<int> ExecuteRunsAsync(CancellationToken ct)
    {
        List<ClaimedRun> claimed;
        using (var scope = services.CreateScope())
        using (RlsScope.Platform())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppsDbContext>();
            claimed = await db.Database.SqlQueryRaw<ClaimedRun>(ClaimRunsSql).ToListAsync(ct);
        }
        await Task.WhenAll(claimed.Select(run => ExecuteAsync(run, ct)));
        return claimed.Count;
    }

    private async Task ExecuteAsync(ClaimedRun run, CancellationToken ct)
    {
        try
        {
            var ran = await runner.RunAsync(run.TenantId, DynamicAppsPlugin.PluginId, run.InstanceId,
                (_, sp) => sp.GetRequiredService<FlowExecutor>().RunAsync(run.Id, ct), ct);
            if (!ran)
            {
                await EndAsync(run, FlowRunStatus.Terminated, "The app was switched off or removed before the run started.");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The executor records step failures itself; reaching here is the engine failing.
            // The lease ends and another pass picks the run up, within its attempts.
            logger.LogWarning(ex, "Flow run {RunId} could not be executed.", run.Id);
        }
    }

    private async Task EndAsync(ClaimedRun run, FlowRunStatus status, string error)
    {
        using var scope = services.CreateScope();
        using var rls = RlsScope.Tenant(run.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppsDbContext>();
        // Set-based: a run that never started has no audit entry of its own to carry.
        await db.FlowRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, status)
            .SetProperty(r => r.Error, error)
            .SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(r => r.FinishedAt, DateTimeOffset.UtcNow));
    }

    // ------------------------------------------------------------------ retention

    /// <summary>Routed events after a week, finished runs after thirty days.</summary>
    private async Task CleanupAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        using var rls = RlsScope.Platform();
        var db = scope.ServiceProvider.GetRequiredService<AppsDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();
        using var bulk = scope.ServiceProvider.GetRequiredService<AuditScope>().SuppressBulkCapture();
        var events = DateTimeOffset.UtcNow.AddDays(-7);
        var runs = DateTimeOffset.UtcNow.AddDays(-30);
        var oldRuns = db.FlowRuns.IgnoreQueryFilters().Where(r => r.FinishedAt < runs).Select(r => r.Id);
        var steps = await db.FlowRunSteps.IgnoreQueryFilters().Where(s => oldRuns.Contains(s.RunId)).ExecuteDeleteAsync(ct);
        var deletedRuns = await db.FlowRuns.IgnoreQueryFilters().Where(r => r.FinishedAt < runs).ExecuteDeleteAsync(ct);
        var deletedEvents = await db.Outbox.Where(m => m.SentAt < events).ExecuteDeleteAsync(ct);
        if (deletedRuns + deletedEvents > 0)
        {
            audit.Record($"plugin.{DynamicAppsPlugin.PluginId}.retention")
                .With("runs", deletedRuns).With("steps", steps).With("events", deletedEvents);
            await audit.FlushAsync(ct);
        }
    }
}
