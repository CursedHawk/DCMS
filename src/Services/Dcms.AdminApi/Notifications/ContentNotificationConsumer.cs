using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Security;
using Dcms.Shared.Telemetry;
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

    protected override Task<NotificationRequest?> MapAsync(
        ContentPublished evt, IServiceProvider scope, CancellationToken ct) =>
        Task.FromResult<NotificationRequest?>(new NotificationRequest(
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
            ResourceId: evt.ContentItemId));
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
        Task.FromResult<NotificationRequest?>(new NotificationRequest(
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
}
