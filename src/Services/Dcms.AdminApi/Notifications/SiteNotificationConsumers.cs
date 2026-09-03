using Dcms.AdminApi.Sites;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Security;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// "Your site is live." The event that closes the publish loop — until now nothing told the
/// admin SPA a build had finished, so the deployments view polled and the admin refreshed.
/// </summary>
public sealed class SitePublishedNotificationConsumer(
    IServiceProvider services,
    INatsJSContext jetStream,
    DcmsMetrics metrics,
    ILogger<SitePublishedNotificationConsumer> logger)
    : NotificationConsumerBase<SitePublished>(services, jetStream, metrics, logger)
{
    protected override string Stream => Streams.SitesEvents;
    protected override string Subject => Subjects.SitePublished;
    protected override string DurableName => "admin-api-notify-site-published";

    protected override async Task<NotificationRequest?> MapAsync(
        SitePublished evt, IServiceProvider scope, CancellationToken ct)
    {
        var name = await SiteNameLookup.ResolveAsync(scope, evt.TenantId, evt.SiteId, ct);

        // Before the bell: the IDE's Deployments panel is watching this build right now, and a
        // notification is a different thing from a live status. Deduplication is deliberately
        // NOT applied here -- a redelivered event pushes the same terminal state twice, which
        // costs one redundant refresh, whereas suppressing it would risk a panel that never
        // learns the build finished.
        await SiteBuildBroadcast.TerminalAsync(scope, evt.TenantId, evt.SiteId, evt.BuildId, ct);

        return new NotificationRequest(
            TenantId: evt.TenantId,
            Kind: NotificationKinds.SitePublished,
            Severity: NotificationSeverity.Success,
            RequiredPermission: PlatformPermissions.SitePublish,
            TitleKey: NotificationKinds.TitleKey(NotificationKinds.SitePublished),
            BodyKey: NotificationKinds.BodyKey(NotificationKinds.SitePublished),
            // The BUILD, not the event. A build succeeding is one fact however many times
            // site-builder announces it -- and it announced each build three times until
            // AckHeartbeat stopped the redelivered rebuilds, which is how one publish put
            // three identical "your site is live" rows in the bell.
            DedupeKey: $"site.published:{evt.BuildId:N}",
            Params: new { site = name },
            LinkPath: $"/sites/{evt.SiteId}",
            ResourceType: "site",
            ResourceId: evt.SiteId);
    }
}

/// <summary>"Your site failed to build." Error severity, so it toasts even for the publisher.</summary>
public sealed class SiteBuildFailedNotificationConsumer(
    IServiceProvider services,
    INatsJSContext jetStream,
    DcmsMetrics metrics,
    ILogger<SiteBuildFailedNotificationConsumer> logger)
    : NotificationConsumerBase<SiteBuildFailed>(services, jetStream, metrics, logger)
{
    protected override string Stream => Streams.SitesEvents;
    protected override string Subject => Subjects.SiteBuildFailed;
    protected override string DurableName => "admin-api-notify-site-failed";

    protected override async Task<NotificationRequest?> MapAsync(
        SiteBuildFailed evt, IServiceProvider scope, CancellationToken ct)
    {
        var name = await SiteNameLookup.ResolveAsync(scope, evt.TenantId, evt.SiteId, ct);

        await SiteBuildBroadcast.TerminalAsync(scope, evt.TenantId, evt.SiteId, evt.BuildId, ct);

        return new NotificationRequest(
            TenantId: evt.TenantId,
            Kind: NotificationKinds.SiteBuildFailed,
            Severity: NotificationSeverity.Error,
            RequiredPermission: PlatformPermissions.SitePublish,
            TitleKey: NotificationKinds.TitleKey(NotificationKinds.SiteBuildFailed),
            BodyKey: NotificationKinds.BodyKey(NotificationKinds.SiteBuildFailed),
            DedupeKey: $"site.build.failed:{evt.BuildId:N}",
            // Truncated: a build error can be a whole compiler dump, and this string is
            // rendered inside a popover. The full log stays behind the link.
            Params: new { site = name, reason = Truncate(evt.Reason, 200) },
            LinkPath: $"/sites/{evt.SiteId}",
            ResourceType: "site",
            ResourceId: evt.SiteId);
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "…";
}

internal static class SiteNameLookup
{
    /// <summary>
    /// The site's display name for the notification body. Consumers have no ambient tenant,
    /// so this names the tenant explicitly rather than relying on the query filter. Falls
    /// back to the id when the site has since been deleted — a notification about a deleted
    /// site is still worth showing, just with less to say.
    /// </summary>
    public static async Task<string> ResolveAsync(
        IServiceProvider scope, Guid tenantId, Guid siteId, CancellationToken ct)
    {
        var db = scope.GetRequiredService<SitesDbContext>();
        var name = await db.Sites.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.Id == siteId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(name) ? siteId.ToString("N")[..8] : name;
    }
}

/// <summary>
/// Pushes a finished build's row to everyone with that site open.
///
/// <para>Read back from the database rather than assembled from the event, because the two
/// consumers here see only "published" and "failed" while the panel renders the whole row —
/// the log key, the completion time, and whether this build is the one now serving the site.
/// Guessing any of those from the event would put a subtly wrong row on screen next to the
/// right one from the next refresh.</para>
///
/// <para>Swallows everything. A failed push means somebody's panel refreshes a little later;
/// letting it throw would fail the message and re-run a notification that already landed.</para>
/// </summary>
internal static class SiteBuildBroadcast
{
    public static async Task TerminalAsync(
        IServiceProvider scope, Guid tenantId, Guid siteId, Guid buildId, CancellationToken ct)
    {
        try
        {
            var db = scope.GetRequiredService<SitesDbContext>();
            var build = await db.Builds.AsNoTracking().IgnoreQueryFilters()
                .FirstOrDefaultAsync(b => b.Id == buildId && b.SiteId == siteId, ct);
            if (build is null) return;

            var activeBuildId = await db.Sites.AsNoTracking().IgnoreQueryFilters()
                .Where(s => s.Id == siteId && s.TenantId == tenantId)
                .Select(s => s.ActiveBuildId)
                .FirstOrDefaultAsync(ct);

            var live = scope.GetRequiredService<ISiteLiveUpdates>();
            await live.BuildChangedAsync(tenantId, new BuildUpdate(
                SiteId: siteId,
                Id: build.Id,
                Status: build.Status.ToString(),
                GitCommitSha: build.GitCommitSha,
                ShortSha: SiteLiveUpdates.Short(build.GitCommitSha),
                Error: build.Error,
                HasLog: build.LogObjectKey is not null,
                CreatedAt: build.CreatedAt,
                CompletedAt: build.CompletedAt,
                Active: activeBuildId == build.Id,
                ActorUserId: null), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(SiteBuildBroadcast))
                .LogWarning(ex, "Could not push the finished state of build {Build}.", buildId);
        }
    }
}
