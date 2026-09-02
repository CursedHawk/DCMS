using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Security;
using Dcms.Shared.Telemetry;
using NATS.Client.JetStream;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// "Your domain is verified and serving." The step a tenant waits on before a site is
/// reachable, and one whose completion is driven by DNS rather than by a click, so there is
/// no request to report it back on.
/// </summary>
public sealed class DomainVerifiedNotificationConsumer(
    IServiceProvider services,
    INatsJSContext jetStream,
    DcmsMetrics metrics,
    ILogger<DomainVerifiedNotificationConsumer> logger)
    : NotificationConsumerBase<TenantDomainVerified>(services, jetStream, metrics, logger)
{
    protected override string Stream => Streams.Tenancy;
    protected override string Subject => Subjects.TenantDomainVerified;
    protected override string DurableName => "admin-api-notify-domain-verified";

    protected override Task<NotificationRequest?> MapAsync(
        TenantDomainVerified evt, IServiceProvider scope, CancellationToken ct) =>
        Task.FromResult<NotificationRequest?>(new NotificationRequest(
            TenantId: evt.TenantId,
            Kind: NotificationKinds.DomainVerified,
            Severity: NotificationSeverity.Success,
            RequiredPermission: PlatformPermissions.DomainsManage,
            TitleKey: NotificationKinds.TitleKey(NotificationKinds.DomainVerified),
            BodyKey: NotificationKinds.BodyKey(NotificationKinds.DomainVerified),
            DedupeKey: evt.EventId.ToString("N"),
            Params: new { hostname = evt.Hostname },
            LinkPath: "/domains",
            ResourceType: "domain",
            ResourceId: evt.DomainId));
}

/// <summary>
/// "A plugin was enabled or disabled." Worth surfacing because enabling a plugin changes
/// what the tenant's sites do and what permissions exist, and it can be done by any of
/// several admins.
/// </summary>
public sealed class PluginInstanceNotificationConsumer(
    IServiceProvider services,
    INatsJSContext jetStream,
    DcmsMetrics metrics,
    ILogger<PluginInstanceNotificationConsumer> logger)
    : NotificationConsumerBase<PluginInstanceChanged>(services, jetStream, metrics, logger)
{
    protected override string Stream => Streams.Tenancy;
    protected override string Subject => Subjects.PluginInstanceChanged;
    protected override string DurableName => "admin-api-notify-plugin-changed";

    protected override Task<NotificationRequest?> MapAsync(
        PluginInstanceChanged evt, IServiceProvider scope, CancellationToken ct)
    {
        // Created/Updated fire on every settings save and would drown the bell. Only the
        // two transitions that change what the tenant's sites actually do are notified.
        if (evt.Kind is not (PluginInstanceChangeKind.Enabled or PluginInstanceChangeKind.Disabled))
        {
            return Task.FromResult<NotificationRequest?>(null);
        }

        return Task.FromResult<NotificationRequest?>(new NotificationRequest(
            TenantId: evt.TenantId,
            Kind: NotificationKinds.PluginInstanceChanged,
            Severity: NotificationSeverity.Info,
            RequiredPermission: PlatformPermissions.PluginsManage,
            TitleKey: NotificationKinds.TitleKey(NotificationKinds.PluginInstanceChanged),
            BodyKey: NotificationKinds.BodyKey(NotificationKinds.PluginInstanceChanged),
            DedupeKey: evt.EventId.ToString("N"),
            Params: new { plugin = evt.PluginId, state = evt.Kind.ToString() },
            LinkPath: "/plugins",
            ResourceType: "plugin_instance",
            ResourceId: evt.InstanceId));
    }
}
