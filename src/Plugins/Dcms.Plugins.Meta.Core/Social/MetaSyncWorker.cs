using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Social;
using Microsoft.EntityFrameworkCore;
using Dcms.Shared.Data.Rls;

namespace Dcms.Plugins.Meta.Core;

/// <summary>
/// Keeps every enabled Instagram and Facebook feed up to date.
///
/// <para><b>One replica per pass</b>, elected by a Postgres advisory lock. Meta's rate limits
/// are per app, not per replica, so N replicas syncing the same tenants would spend the budget
/// N times over and take every tenant's feed down together.</para>
///
/// <para><b>Failure is per instance and never fatal.</b> One tenant with a revoked token must
/// not stop the pass, and admin-api must not fall over because Meta is having an afternoon.
/// Repeated failures push <c>NextAttemptAt</c> out exponentially, so a permanently broken
/// connection costs one request an hour rather than one every fifteen minutes forever.</para>
/// </summary>
public sealed class MetaSyncWorker(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<MetaSyncWorker> logger) : BackgroundService
{
    /// <summary>How often to look for due instances; each instance's own interval gates it.</summary>
    private TimeSpan PollInterval =>
        TimeSpan.FromSeconds(configuration.GetValue("Social:PollSeconds", 60));

    private const int MaxBackoffMinutes = 60;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup already runs migrations, the RLS configurator and the audit maintenance
        // pass; adding outbound HTTP to that is how a deploy times out its own health check.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Meta sync pass failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrEmpty(connectionString)) return;

        await using var lease = await PostgresAdvisoryLock.TryAcquireAsync(
            connectionString, PostgresAdvisoryLock.MetaSyncLockKey, logger, ct);

        // Another replica has the pass. Not an error — that is the design.
        if (lease is null) return;

        using var scope = services.CreateScope();
        var cms = scope.ServiceProvider.GetRequiredService<CmsDbContext>();
        var runner = scope.ServiceProvider.GetRequiredService<PluginHandlerRunner>();

        // Cross-tenant by design, so the tenant query filters have to be off: this job runs on
        // a timer with no request behind it and therefore no ambient tenant. Platform scope for
        // the scan; SyncOneAsync narrows to each instance's tenant (ADR 0015).
        using var rls = RlsScope.Platform();
        var instances = await cms.PluginInstances.IgnoreQueryFilters()
            .Where(p => p.Enabled && MetaPlugins.FeedPluginIds.Contains(p.PluginId))
            .ToListAsync(ct);

        foreach (var instance in instances)
        {
            if (ct.IsCancellationRequested) break;
            // Each instance runs as its own plugin — the feed plugin's dcms.media@1 imports the
            // mirrored files — in a scope of its own, with the instance's tenant ambient.
            await runner.RunAsync(instance.TenantId, instance.PluginId, instance.Id, (plugin, sp) =>
                SyncOneAsync(instance, sp.GetRequiredService<SocialDbContext>(),
                    sp.GetRequiredService<MetaFeedSyncService>(), plugin.Contracts.Get<IPluginMedia>(), ct), ct);
        }
    }

    private async Task SyncOneAsync(
        PluginInstance instance, SocialDbContext social, MetaFeedSyncService sync, IPluginMedia media, CancellationToken ct)
        {
            using var rls = RlsScope.Tenant(instance.TenantId);
            var settings = MetaFeedSettings.Read(instance.ConfigJson);
        if (settings is null) return;

        // One state row per instance drives the schedule. The per-content-type rows the sync
        // service keeps are for reporting; the gate is here so a failing instance backs off as
        // a whole rather than once per content type.
        var state = await social.SyncStates.IgnoreQueryFilters().FirstOrDefaultAsync(
            s => s.TenantId == instance.TenantId && s.PluginInstanceId == instance.Id
                 && s.ContentType == string.Empty, ct);

        if (state is null)
        {
            state = new MetaSyncState
            {
                Id = Guid.NewGuid(),
                TenantId = instance.TenantId,
                PluginInstanceId = instance.Id,
                ConnectionId = settings.ConnectionId,
                ContentType = string.Empty,
                NextAttemptAt = DateTimeOffset.UtcNow,
            };
            social.SyncStates.Add(state);
        }

        if (state.NextAttemptAt > DateTimeOffset.UtcNow) return;

        var outcome = await sync.SyncInstanceAsync(instance, media, ct);

        state.ConnectionId = settings.ConnectionId;
        state.LastSyncAt = DateTimeOffset.UtcNow;

        if (outcome.Ok)
        {
            state.ConsecutiveFailures = 0;
            state.LastError = null;
            state.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(settings.SyncIntervalMinutes);

            if (outcome.Created > 0 || outcome.Updated > 0 || outcome.Trimmed > 0)
            {
                logger.LogInformation(
                    "Meta sync for instance {InstanceId}: +{Created} ~{Updated} -{Trimmed} in {Pages} request(s).",
                    instance.Id, outcome.Created, outcome.Updated, outcome.Trimmed, outcome.PagesFetched);
            }
        }
        else
        {
            state.ConsecutiveFailures++;
            state.LastError = outcome.Error;

            // Exponential, capped. A connection whose token was revoked will fail every time
            // until somebody reconnects it, and retrying that at the configured interval
            // forever is a self-inflicted rate-limit problem.
            var backoff = Math.Min(
                settings.SyncIntervalMinutes * Math.Pow(2, Math.Min(state.ConsecutiveFailures, 6)),
                MaxBackoffMinutes);
            state.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(backoff);

            logger.LogWarning(
                "Meta sync for instance {InstanceId} failed ({Failures} in a row): {Error}",
                instance.Id, state.ConsecutiveFailures, outcome.Error);
        }

        await social.SaveChangesAsync(ct);
    }
}
