using System.Text.Json;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>
/// The automation engine's background loops, in admin-api (ADR 0021): one routes outbox events
/// to the flows they trigger and starts due schedules, the other claims runnable runs and
/// executes them. Each repeats at once while it finds a full batch, else waits for the tick. The database is the queue throughout: rows are claimed with
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

    private static readonly TimeSpan IndexSyncEvery = TimeSpan.FromSeconds(10);
    private const int IndexOpsPerCall = 4;
    private static readonly TimeSpan IndexCallTimeout = TimeSpan.FromSeconds(15); // = the SET LOCAL below
    private DateTimeOffset _nextIndexSync = DateTimeOffset.UtcNow.AddSeconds(5);
    private string? _indexedRevisions;

    /// <summary>
    /// Two loops: routing (with schedules and cleanup) and running. Apart, so a long routing pass
    /// over a backlog does not hold runs back; each goes again at once while it finds full batches.
    /// Paced by the tick alone, they were capped at 100 events and 4 runs a second whatever the
    /// host could do — an import's 84k events took 14 minutes to route on vps1.
    /// </summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        LoopAsync(async ct =>
        {
            var routed = await RouteAsync(ct);
            await ScheduleAsync(ct);
            if (DateTimeOffset.UtcNow >= _nextCleanup)
            {
                await CleanupAsync(ct);
                _nextCleanup = DateTimeOffset.UtcNow.AddHours(1);
            }
            if (DateTimeOffset.UtcNow >= _nextIndexSync)
            {
                // Set first: a failing sync is retried at its pace, not every tick.
                _nextIndexSync = DateTimeOffset.UtcNow.Add(IndexSyncEvery);
                if (await SyncIndexesAsync(ct))
                {
                    _nextIndexSync = DateTimeOffset.UtcNow.AddSeconds(2);
                }
            }
            return routed == OutboxBatch;
        }, stoppingToken),
        RunSlotsAsync(stoppingToken));

    /// <summary>Runs <paramref name="pass"/> every tick, and again at once while it reports more waiting.</summary>
    private async Task LoopAsync(Func<CancellationToken, Task<bool>> pass, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        do
        {
            var more = true;
            while (more && !stoppingToken.IsCancellationRequested)
            {
                try
                {
                    more = await pass(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    more = false;
                    logger.LogWarning(ex, "Dynamic Apps automation pass failed; retrying.");
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // ------------------------------------------------------------------ indexes

    /// <summary>
    /// Brings the indexes behind sortable and filterable fields (RecordIndexes) in line with every
    /// published revision, when one has changed since the last complete pass. Returns true while work
    /// is left: the per-call budget ran out, a lock was not granted, or another replica is at it.
    /// </summary>
    /// <remarks>
    /// Index DDL holds apps.records for every tenant, so the database rations its time for the whole
    /// platform (apps.sync_record_indexes: 2 minutes an hour, at most a tenth of the time). Here: at
    /// most <see cref="IndexOpsPerCall"/> builds or drops a call, each call cut off after
    /// <see cref="IndexCallTimeout"/> and charged in full when it is, and the tenants' indexes taken in
    /// turn, so one tenant toggling flags delays its own indexes, not everyone's.
    /// </remarks>
    public async Task<bool> SyncIndexesAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        using var platform = RlsScope.Platform();
        var db = scope.ServiceProvider.GetRequiredService<AppsDbContext>();
        var live = await (from r in db.Revisions.AsNoTracking().IgnoreQueryFilters() // rls: RlsScope.Platform, every tenant's published revision
                          join a in db.Apps.AsNoTracking().IgnoreQueryFilters() on r.AppId equals a.Id // rls: as above
                          where r.Status == AppRevisionStatus.Published
                          orderby r.TenantId, a.CreatedAt, a.Id
                          select new { r.Id, r.Hash, r.TenantId, a.InstanceId })
            .ToListAsync(ct);
        var state = string.Join(",", live.Select(r => $"{r.Id:N}:{r.Hash}"));
        if (state == _indexedRevisions)
        {
            return false;
        }
        var perTenant = new List<List<RecordIndexes.Spec>>();
        foreach (var tenant in live.GroupBy(r => r.TenantId))
        {
            var mine = new List<RecordIndexes.Spec>();
            foreach (var revision in tenant)
            {
                var snapshot = await db.Revisions.AsNoTracking().IgnoreQueryFilters() // rls: RlsScope.Platform, as above
                    .Where(r => r.Id == revision.Id).Select(r => r.Snapshot).SingleAsync(ct);
                mine.AddRange(RecordIndexes.For(ConfigJson.Parse(snapshot), revision.TenantId, revision.InstanceId));
            }
            perTenant.Add(mine.Take(RecordIndexes.MaxPerTenant).ToList());
        }
        // Round robin over tenants: the function builds in this order, so each tenant gets a turn.
        var wanted = Enumerable.Range(0, perTenant.Count == 0 ? 0 : perTenant.Max(t => t.Count))
            .SelectMany(i => perTenant.Where(t => i < t.Count).Select(t => t[i]))
            .ToList();
        var json = JsonSerializer.Serialize(wanted.Select(w => new
        {
            name = w.Name,
            tenant = w.TenantId.ToString(),
            instance = w.InstanceId.ToString(),
            table = w.TableId.ToString(),
            key = w.Key,
            kind = w.Kind switch { RecordIndexes.KeyKind.Number => "number", RecordIndexes.KeyKind.Search => "search", _ => "text" },
            descending = w.Descending,
        }));

        List<string> report;
        try
        {
            // Here, not in the function: a statement_timeout set inside it would not apply to the
            // statement already running.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // A constant: SET takes no parameters.
            const string limit = "SET LOCAL statement_timeout = 15000";
            await db.Database.ExecuteSqlRawAsync(limit, ct);
            report = await db.Database
                .SqlQuery<string>($"SELECT unnest(apps.sync_record_indexes({json}::jsonb, {IndexOpsPerCall})) AS \"Value\"")
                .ToListAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (PostgresException e)
        {
            // The call as a whole failed. Cancelled, its rollback took its accounting with it: charge
            // the time it held, so the ration still holds. Not again until a revision changes.
            if (e.SqlState == PostgresErrorCodes.QueryCanceled)
            {
                await db.Database.ExecuteSqlAsync($"SELECT apps.record_index_charge({(int)IndexCallTimeout.TotalMilliseconds})", ct);
            }
            logger.LogWarning("Dynamic Apps index sync failed with SQLSTATE {SqlState}; it waits for the next publish.", e.SqlState);
            _indexedRevisions = state;
            return false;
        }
        var done = report.Count(l => l.StartsWith("created", StringComparison.Ordinal) || l.StartsWith("dropped", StringComparison.Ordinal));
        foreach (var line in report.Where(l => l.StartsWith("failed", StringComparison.Ordinal)))
        {
            logger.LogWarning("Dynamic Apps index sync: {Line}", line); // index name and SQLSTATE only
        }
        if (done > 0)
        {
            logger.LogInformation("Dynamic Apps index sync: {Done} built or dropped, {Wanted} wanted.", done, wanted.Count);
        }
        // Retried soon: the budget or the ration ran out, another replica holds the lock, or a lock was
        // not granted (55P03). Any other failure waits for the next publish.
        var more = report.Any(l => l is "more" or "busy" or "wait" || (l.StartsWith("failed", StringComparison.Ordinal) && l.EndsWith(" 55P03", StringComparison.Ordinal)));
        if (!more)
        {
            _indexedRevisions = state;
        }
        return more;
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
            ORDER BY "NextAttemptAt" FOR UPDATE SKIP LOCKED LIMIT {"{0}"})
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

    /// <summary>
    /// Keeps <see cref="Concurrency"/> runs going (<see cref="RunSlots"/>). Claiming four and waiting
    /// for all four let one slow script hold three slots idle: 10 % of runs at the 2 s script limit cut
    /// 14.2 runs/s to 4.1 (load test 2026-10-21, finding 7).
    /// </summary>
    private Task RunSlotsAsync(CancellationToken stoppingToken) => RunSlots.RunAsync(
        Concurrency,
        ClaimRunsAsync,
        run => ExecuteAsync(run, stoppingToken),
        Tick,
        ex => logger.LogWarning(ex, "Dynamic Apps automation pass failed; retrying."),
        stoppingToken);

    /// <summary>Claims up to <paramref name="max"/> runnable runs: due pending ones, and running ones whose lease ran out.</summary>
    private async Task<IReadOnlyCollection<ClaimedRun>> ClaimRunsAsync(int max, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        using var platform = RlsScope.Platform();
        var db = scope.ServiceProvider.GetRequiredService<AppsDbContext>();
        return await db.Database.SqlQueryRaw<ClaimedRun>(ClaimRunsSql, max).ToListAsync(ct);
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
