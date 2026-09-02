using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Telemetry;
using NATS.Client.JetStream;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// Drains <c>notify.raise</c> — the inbound half of the notification system, for services
/// that cannot write the notifications schema. Same shape as audit's <c>audit.submitted</c>
/// ingest, and for the same reason: one writer for the tables, reached either in-process or
/// over the bus.
///
/// <para>Today the only publisher is content-api, for form submissions, which have no NATS
/// subject of their own and previously produced nothing but an optional email.</para>
/// </summary>
public sealed class NotificationIngestConsumer(
    IServiceProvider services,
    INatsJSContext jetStream,
    DcmsMetrics metrics,
    ILogger<NotificationIngestConsumer> logger)
    : NotificationConsumerBase<NotificationRaiseRequested>(services, jetStream, metrics, logger)
{
    protected override string Stream => Streams.Notify;
    protected override string Subject => Subjects.NotifyRaise;
    protected override string DurableName => "admin-api-notify-ingest";

    protected override Task<NotificationRequest?> MapAsync(
        NotificationRaiseRequested evt, IServiceProvider scope, CancellationToken ct)
    {
        // An unparseable severity is downgraded rather than dropped. Refusing a notification
        // because a sender spelled its severity wrong loses the message entirely, which is
        // strictly worse than showing it one level quieter than intended.
        if (!Enum.TryParse<NotificationSeverity>(evt.Severity, ignoreCase: true, out var severity))
        {
            logger.LogWarning("notify.raise carried unknown severity {Severity}; treating as Info.", evt.Severity);
            severity = NotificationSeverity.Info;
        }

        return Task.FromResult<NotificationRequest?>(new NotificationRequest(
            TenantId: evt.TenantId,
            Kind: evt.Kind,
            Severity: severity,
            RequiredPermission: evt.RequiredPermission,
            TitleKey: evt.TitleKey,
            BodyKey: evt.BodyKey,
            // The sender's key identifies the underlying occurrence, not this message, so a
            // JetStream redelivery collapses onto the same row.
            DedupeKey: evt.DedupeKey,
            Params: null,
            LinkPath: evt.LinkPath,
            ResourceType: evt.ResourceType,
            ResourceId: evt.ResourceId,
            ActorUserId: evt.ActorUserId)
        {
            // ParamsJson arrives pre-serialised from the sender; NotificationRequest.Params
            // is an object the publisher serialises, so it is passed through as raw JSON.
            Params = new RawJson(evt.ParamsJson),
        });
    }
}

/// <summary>
/// Carries already-serialised JSON through <c>NotificationRequest.Params</c> without a
/// deserialise/reserialise round trip that would reorder keys and reject anything the
/// sender's shape does not model.
/// </summary>
public sealed class RawJson(string json)
{
    public string Json { get; } = string.IsNullOrWhiteSpace(json) ? "{}" : json;
}
