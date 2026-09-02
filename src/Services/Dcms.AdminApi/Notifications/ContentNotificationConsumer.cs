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
            LinkPath: $"/content/{evt.PluginInstanceId}",
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
            LinkPath: $"/content/{evt.PluginInstanceId}",
            ResourceType: "content_item",
            ResourceId: evt.ContentItemId));
}
