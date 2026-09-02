using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Security;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// "Transcoding failed and your asset is unusable." <c>MediaConsumerBase</c> says of this
/// state that "the upload succeeded and the asset is unusable — a state the tenant will
/// notice and ask about"; until now nothing told them.
///
/// <para>Note this is the first consumer of <c>MEDIA_EVENTS</c> at all: the stream has been
/// provisioned and published to, with nothing reading it.</para>
/// </summary>
public sealed class MediaFailedNotificationConsumer(
    IServiceProvider services,
    INatsJSContext jetStream,
    DcmsMetrics metrics,
    ILogger<MediaFailedNotificationConsumer> logger)
    : NotificationConsumerBase<MediaFailed>(services, jetStream, metrics, logger)
{
    protected override string Stream => Streams.MediaEvents;
    protected override string Subject => Subjects.MediaFailed;
    protected override string DurableName => "admin-api-notify-media-failed";

    protected override async Task<NotificationRequest?> MapAsync(
        MediaFailed evt, IServiceProvider scope, CancellationToken ct)
    {
        var name = await MediaNameLookup.ResolveAsync(scope, evt.TenantId, evt.AssetId, ct);

        return new NotificationRequest(
            TenantId: evt.TenantId,
            Kind: NotificationKinds.MediaFailed,
            Severity: NotificationSeverity.Error,
            RequiredPermission: PlatformPermissions.MediaWrite,
            TitleKey: NotificationKinds.TitleKey(NotificationKinds.MediaFailed),
            BodyKey: NotificationKinds.BodyKey(NotificationKinds.MediaFailed),
            DedupeKey: evt.EventId.ToString("N"),
            Params: new { file = name, reason = evt.Reason },
            LinkPath: "/media",
            ResourceType: "media_asset",
            ResourceId: evt.AssetId);
    }
}

/// <summary>
/// "Your upload finished processing." Info rather than Success, and combined with the
/// self-suppression rule that means the person who uploaded it gets a bell entry and no
/// toast — which is the point, since a bulk upload would otherwise toast once per file.
/// </summary>
public sealed class MediaProcessedNotificationConsumer(
    IServiceProvider services,
    INatsJSContext jetStream,
    DcmsMetrics metrics,
    ILogger<MediaProcessedNotificationConsumer> logger)
    : NotificationConsumerBase<MediaProcessed>(services, jetStream, metrics, logger)
{
    protected override string Stream => Streams.MediaEvents;
    protected override string Subject => Subjects.MediaProcessed;
    protected override string DurableName => "admin-api-notify-media-processed";

    protected override async Task<NotificationRequest?> MapAsync(
        MediaProcessed evt, IServiceProvider scope, CancellationToken ct)
    {
        var db = scope.GetRequiredService<MediaDbContext>();
        var asset = await db.Assets.AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.TenantId == evt.TenantId && a.Id == evt.AssetId)
            .Select(a => new { a.FileName, a.CreatedBy })
            .FirstOrDefaultAsync(ct);

        return new NotificationRequest(
            TenantId: evt.TenantId,
            Kind: NotificationKinds.MediaProcessed,
            Severity: NotificationSeverity.Info,
            RequiredPermission: PlatformPermissions.MediaWrite,
            TitleKey: NotificationKinds.TitleKey(NotificationKinds.MediaProcessed),
            BodyKey: NotificationKinds.BodyKey(NotificationKinds.MediaProcessed),
            DedupeKey: evt.EventId.ToString("N"),
            Params: new { file = asset?.FileName ?? evt.AssetId.ToString("N")[..8] },
            LinkPath: "/media",
            ResourceType: "media_asset",
            ResourceId: evt.AssetId,
            // The uploader is the actor, so their own upload does not toast at them.
            ActorUserId: asset?.CreatedBy);
    }
}

internal static class MediaNameLookup
{
    public static async Task<string> ResolveAsync(
        IServiceProvider scope, Guid tenantId, Guid assetId, CancellationToken ct)
    {
        var db = scope.GetRequiredService<MediaDbContext>();
        var name = await db.Assets.AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.Id == assetId)
            .Select(a => a.FileName)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(name) ? assetId.ToString("N")[..8] : name;
    }
}
