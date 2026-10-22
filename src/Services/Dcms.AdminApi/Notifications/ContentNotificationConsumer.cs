using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Security;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// "An item went live." Most valuable for the publishes nobody watched happen — a scheduled
/// publish firing at 03:00, or the Meta feed sync importing a post — which is why this
/// consumes the event rather than hooking the endpoint: both paths already converge on
/// <c>content.published</c> via the CMS outbox.
/// </summary>
public sealed class ContentPublishedNotificationConsumer(
    IServiceProvider services,
    INatsJSContext jetStream,
    DcmsMetrics metrics,
    ILogger<ContentPublishedNotificationConsumer> logger)
    : NotificationConsumerBase<ContentPublished>(services, jetStream, metrics, logger)
{
    protected override string Stream => Streams.Cms;
    protected override string Subject => Subjects.ContentPublished;
    protected override string DurableName => "admin-api-notify-content-published";

    /// <summary>
    /// Publishes this close together are one piece of news: within this long of a tenant's last
    /// "went live", the next one joins it ("25 items are now live") instead of making its own. A
    /// bulk publish was otherwise one bell entry per item: 28,459 publishes, 28,596 notifications
    /// in the 2026-10-21 load test (finding 9).
    /// </summary>
    internal static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(1);

    /// <summary>Item ids a burst remembers, so a redelivered publish is not counted twice. Past this, it only counts.</summary>
    private const int BurstItemsKept = 500;

    protected override async Task<NotificationRequest?> MapAsync(
        ContentPublished evt, IServiceProvider scope, CancellationToken ct) =>
        evt.Quiet || await JoinBurstAsync(evt, scope, ct) ? null : Single(evt);

    /// <summary>
    /// Adds the publish to the tenant's "went live" notification of the last <see cref="BurstWindow"/>,
    /// if there is one. False when there is none, and the publish is news of its own.
    /// </summary>
    internal static async Task<bool> JoinBurstAsync(ContentPublished evt, IServiceProvider scope, CancellationToken ct)
    {
        // One consumer, so no two publishes of a tenant are merged at once; a second replica would
        // need a lock here.
        var db = scope.GetRequiredService<NotificationsDbContext>();
        var since = DateTimeOffset.UtcNow - BurstWindow;
        var recent = await db.Notifications.IgnoreQueryFilters() // rls: RlsScope.Tenant(evt.TenantId), set by the consumer base
            .Where(n => n.TenantId == evt.TenantId && n.Kind == NotificationKinds.ContentPublished && n.CreatedAt >= since)
            .OrderByDescending(n => n.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (recent is null)
        {
            return false;
        }

        var burst = JsonNode.Parse(recent.ParamsJson) as JsonObject ?? [];
        var items = burst["items"] is JsonArray kept
            ? kept.Select(x => x!.GetValue<Guid>()).ToList()
            : recent.ResourceId is { } first ? [first] : [];
        if (items.Contains(evt.ContentItemId))
        {
            return true; // a redelivery of a publish this burst already counted
        }
        var count = (burst["count"]?.GetValue<int>() ?? items.Count) + 1;
        if (items.Count < BurstItemsKept)
        {
            items.Add(evt.ContentItemId);
        }

        recent.TitleKey = NotificationKinds.TitleKey(BurstKey);
        recent.BodyKey = NotificationKinds.BodyKey(BurstKey);
        recent.ParamsJson = JsonSerializer.Serialize(new { count, items });
        recent.LinkPath = ContentLink.ForList(evt.PluginInstanceId, evt.ContentType);
        recent.ResourceId = null;
        // Unread again for whoever has not dismissed it: there is more news than they saw.
        var rows = await db.Recipients.IgnoreQueryFilters() // rls: as above
            .Where(r => r.TenantId == evt.TenantId && r.NotificationId == recent.Id && r.DismissedAt == null).ToListAsync(ct);
        rows.ForEach(r => r.ReadAt = null);
        await db.SaveChangesAsync(ct);
        await scope.GetRequiredService<NotificationPublisher>().PushUpdatedAsync(recent, rows, ct);
        return true;
    }

    private const string BurstKey = NotificationKinds.ContentPublishedMany;

    internal static NotificationRequest Single(ContentPublished evt) => new(
            TenantId: evt.TenantId,
            Kind: NotificationKinds.ContentPublished,
            Severity: NotificationSeverity.Info,
            RequiredPermission: PlatformPermissions.ContentPublish,
            TitleKey: NotificationKinds.TitleKey(NotificationKinds.ContentPublished),
            BodyKey: NotificationKinds.BodyKey(NotificationKinds.ContentPublished),
            // The item plus the moment: an item can legitimately be published again later
            // and that is news, but a redelivery of one publish carries the same
            // OccurredAt and must not be.
            DedupeKey: $"content.published:{evt.ContentItemId:N}:{evt.OccurredAt.UtcTicks}",
            Params: new { slug = evt.Slug, contentType = evt.ContentType },
            LinkPath: ContentLink.For(evt.PluginInstanceId, evt.ContentType, evt.ContentItemId),
            ResourceType: "content_item",
            ResourceId: evt.ContentItemId);
}

/// <summary>
/// "An item came down." The counterpart to publishing, and the more surprising of the two:
/// content disappearing from a live site is what someone notices and asks about.
/// </summary>
public sealed class ContentUnpublishedNotificationConsumer(
    IServiceProvider services,
    INatsJSContext jetStream,
    DcmsMetrics metrics,
    ILogger<ContentUnpublishedNotificationConsumer> logger)
    : NotificationConsumerBase<ContentUnpublished>(services, jetStream, metrics, logger)
{
    protected override string Stream => Streams.Cms;
    protected override string Subject => Subjects.ContentUnpublished;
    protected override string DurableName => "admin-api-notify-content-unpublished";

    protected override Task<NotificationRequest?> MapAsync(
        ContentUnpublished evt, IServiceProvider scope, CancellationToken ct) =>
        Task.FromResult<NotificationRequest?>(evt.Quiet ? null : new NotificationRequest(
            TenantId: evt.TenantId,
            Kind: NotificationKinds.ContentUnpublished,
            Severity: NotificationSeverity.Warning,
            RequiredPermission: PlatformPermissions.ContentPublish,
            TitleKey: NotificationKinds.TitleKey(NotificationKinds.ContentUnpublished),
            BodyKey: NotificationKinds.BodyKey(NotificationKinds.ContentUnpublished),
            DedupeKey: $"content.unpublished:{evt.ContentItemId:N}:{evt.OccurredAt.UtcTicks}",
            Params: new { slug = evt.Slug, contentType = evt.ContentType },
            LinkPath: ContentLink.For(evt.PluginInstanceId, evt.ContentType, evt.ContentItemId),
            ResourceType: "content_item",
            ResourceId: evt.ContentItemId));
}

/// <summary>
/// Where a content notification points in the admin SPA.
///
/// <para>It used to be <c>/content/{pluginInstanceId}</c>, and there has never been a route
/// that matches it: the SPA has exactly one content route, <c>/content</c>, and an item is
/// edited in a MODAL over the collection list, not on a page of its own. So every "your item
/// is live" notification led to Not Found — the notification worked, the link was the thing
/// that did not, and it had never worked at all.</para>
///
/// <para>Query parameters rather than path segments, because that is what the destination
/// actually is: one page, told which collection to select and which item to open. A path
/// segment would have meant inventing a route for a screen that does not exist.</para>
///
/// <para>The SPA also rewrites the old <c>/content/{guid}</c> form on the way to the router
/// (see <c>resolveLinkPath</c>), because the rows already stored in the notifications table
/// still carry it and are read for as long as the retention window lasts.</para>
/// </summary>
public static class ContentLink
{
    public static string For(Guid pluginInstanceId, string contentType, Guid contentItemId) =>
        $"/content?instance={pluginInstanceId}&type={Uri.EscapeDataString(contentType)}&item={contentItemId}";

    /// <summary>The collection itself, for news about many of its items.</summary>
    public static string ForList(Guid pluginInstanceId, string contentType) =>
        $"/content?instance={pluginInstanceId}&type={Uri.EscapeDataString(contentType)}";
}
