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

        return new NotificationRequest(
            TenantId: evt.TenantId,
            Kind: NotificationKinds.SitePublished,
            Severity: NotificationSeverity.Success,
            RequiredPermission: PlatformPermissions.SitePublish,
            TitleKey: NotificationKinds.TitleKey(NotificationKinds.SitePublished),
            BodyKey: NotificationKinds.BodyKey(NotificationKinds.SitePublished),
            DedupeKey: evt.EventId.ToString("N"),
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

        return new NotificationRequest(
            TenantId: evt.TenantId,
            Kind: NotificationKinds.SiteBuildFailed,
            Severity: NotificationSeverity.Error,
            RequiredPermission: PlatformPermissions.SitePublish,
            TitleKey: NotificationKinds.TitleKey(NotificationKinds.SiteBuildFailed),
            BodyKey: NotificationKinds.BodyKey(NotificationKinds.SiteBuildFailed),
            DedupeKey: evt.EventId.ToString("N"),
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
