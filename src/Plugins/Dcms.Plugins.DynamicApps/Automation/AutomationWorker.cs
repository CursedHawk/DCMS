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

using Dcms.Shared.Data;
namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>
/// Dynamic Apps' background loop, in admin-api's worker role: it hands each committed row and
/// relation event in the outbox to whatever runs on an app's events (Functions, ADR 0029), keeps
/// the indexes behind sortable and filterable fields in line with what is published, and clears
/// routed events after a week. The outbox is claimed with <c>FOR UPDATE SKIP LOCKED</c>, so any
/// number of replicas share it. Flows, which this once also ran, are functions now.
///
/// <para>Each tenant's events go through <see cref="PluginHandlerRunner"/>: its tenant scope, the
/// check that the instance is still enabled, its plugin context and its audit flush.</para>
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

    private DateTimeOffset _nextCleanup = DateTimeOffset.UtcNow.AddMinutes(5);

    private static readonly TimeSpan IndexSyncEvery = TimeSpan.FromSeconds(10);
    private const int IndexOpsPerCall = 4;
    private static readonly TimeSpan IndexCallTimeout = TimeSpan.FromSeconds(15); // = the SET LOCAL below
    private DateTimeOffset _nextIndexSync = DateTimeOffset.UtcNow.AddSeconds(5);
    private string? _indexedRevisions;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        LoopAsync(async ct =>
        {
            var routed = await RouteAsync(ct);
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
        }, stoppingToken);

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
        var live = await (from r in db.Revisions.AsNoTracking().IgnoreTenantFilter() // rls: RlsScope.Platform, every tenant's published revision
                          join a in db.Apps.AsNoTracking().IgnoreTenantFilter() on r.AppId equals a.Id // rls: as above
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
                var snapshot = await db.Revisions.AsNoTracking().IgnoreTenantFilter() // rls: RlsScope.Platform, as above
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

    // ------------------------------------------------------------------ outbox → functions

    private const string ClaimOutboxSql = """
        SELECT * FROM apps.outbox
        WHERE "SentAt" IS NULL
        ORDER BY "OccurredAt"
        FOR UPDATE SKIP LOCKED
        LIMIT 100
        """;

    /// <summary>Hands each pending event to what runs on its app's events. Delivery is idempotent by event id, so a crash before the stamp only repeats work.</summary>
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
                    // In the environment the event happened in (ADR 0028).
                    using (RlsScope.Environment(message.TenantId, message.EnvironmentId))
                    {
                        await runner.RunAsync(message.TenantId, DynamicAppsPlugin.PluginId, message.InstanceId, async (context, sp) =>
                        {
                            // Each sink is idempotent by event id, so a retry of this message repeats nothing.
                            // ponytail: one call per sink per event; batch them if imports make routing slow.
                            foreach (var sink in context.Contracts.GetAll<IAppEventSink>())
                            {
                                await sink.DeliverAsync(new AppEventDelivery(context.Instance!.Slug, evt), ct);
                            }
                        }, ct);
                    }
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

    // ------------------------------------------------------------------ retention

    /// <summary>Routed events after a week.</summary>
    private async Task CleanupAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        using var rls = RlsScope.Platform();
        var db = scope.ServiceProvider.GetRequiredService<AppsDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();
        using var bulk = scope.ServiceProvider.GetRequiredService<AuditScope>().SuppressBulkCapture();
        var deletedEvents = await db.Outbox.Where(m => m.SentAt < DateTimeOffset.UtcNow.AddDays(-7)).ExecuteDeleteAsync(ct);
        if (deletedEvents > 0)
        {
            audit.Record($"plugin.{DynamicAppsPlugin.PluginId}.retention").With("events", deletedEvents);
            await audit.FlushAsync(ct);
        }
    }
}
