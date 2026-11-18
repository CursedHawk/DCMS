using Dcms.AdminApi.Ai;
using Dcms.AdminApi.Analytics;
using Dcms.AdminApi.Audit;
using Dcms.AdminApi.Cms;
using Dcms.AdminApi.Connections;
using Dcms.AdminApi.Environments;
using Dcms.AdminApi.Media;
using Dcms.AdminApi.Notifications;
using Dcms.AdminApi.Sites.Audits;
using Dcms.AdminApi.Tenancy;
using Dcms.AdminApi.Tenancy.Usage;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Jobs;

namespace Dcms.JobsWorker;

/// <summary>
/// What jobs-worker runs, in modules. Today one service calls them all; a later plugin-worker or
/// platform-worker is a service that calls a subset (ADR 0036). No switches until that service
/// exists -- the lanes already have theirs (Jobs:Lanes:{lane}:Enabled).
/// </summary>
public static class JobsWorkerServices
{
    public static IServiceCollection AddJobsWorker(this IServiceCollection services, IConfiguration configuration) => services
        .AddJobLanes(configuration)
        .AddEventConsumers()
        .AddOutboxes()
        .AddSweeps()
        .AddPluginWork();

    /// <summary>The job ledger's two lanes (ADR 0024), their housekeeping, and the admin plane's own job handlers.</summary>
    public static IServiceCollection AddJobLanes(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddJobLedgerHousekeeping();
        services.AddJobConsumer(configuration, JobLanes.Tenant, defaultConcurrency: 4);
        // The platform's own work (no tenant), on its own lane, so a backlog of tenant imports
        // never stands in front of a platform purge.
        services.AddJobConsumer(configuration, JobLanes.Platform, defaultConcurrency: 2);
        services.AddJobHandler<DriveFileJob, DriveFilePayload>(DriveFileJob.Kind);
        services.AddJobHandler<EnvironmentApplyJob, EnvironmentApplyPayload>(EnvironmentApplyJob.Kind);
        services.AddJobHandler<EnvironmentRollbackJob, EnvironmentRollbackPayload>(EnvironmentRollbackJob.Kind);
        services.AddJobHandler<EnvironmentCopyJob, EnvironmentCopyPayload>(EnvironmentCopyJob.Kind);
        services.AddJobHandler<SiteAuditJob, SiteAuditPayload>(SiteAuditJob.Kind);
        return services;
    }

    /// <summary>
    /// Shared durable consumers: each event is handled by exactly one replica, and the SignalR
    /// Redis backplane carries any push to browsers connected to admin-api.
    /// </summary>
    public static IServiceCollection AddEventConsumers(this IServiceCollection services)
    {
        // Permission cache invalidation (Redis) and Owner mirroring across environments.
        services.AddHostedService<MembershipChangedConsumer>();
        // In-app notifications: each consumer turns one already-published subject into one.
        services.AddHostedService<SitePublishedNotificationConsumer>();
        services.AddHostedService<SiteBuildFailedNotificationConsumer>();
        services.AddHostedService<MediaProcessedNotificationConsumer>();
        services.AddHostedService<MediaFailedNotificationConsumer>();
        services.AddHostedService<ContentPublishedNotificationConsumer>();
        services.AddHostedService<ContentUnpublishedNotificationConsumer>();
        services.AddHostedService<DomainVerifiedNotificationConsumer>();
        services.AddHostedService<PluginInstanceNotificationConsumer>();
        services.AddHostedService<NotificationIngestConsumer>();
        // Audit records from the services that cannot reach the audit schema, and the projection
        // of every record onto the log pipeline.
        services.AddHostedService<AuditIngestConsumer>();
        services.AddHostedService<AuditLogProjector>();
        return services;
    }

    /// <summary>Tables drained onto the bus or the hash chain.</summary>
    public static IServiceCollection AddOutboxes(this IServiceCollection services)
    {
        // The CMS outbox, onto content.published/unpublished (SKIP LOCKED).
        services.AddHostedService<OutboxDispatcher>();
        // audit_outbox into the hash chain: a single writer by design (FOR UPDATE, replicas take turns).
        services.AddHostedService<AuditChainWriter>();
        return services;
    }

    /// <summary>Timed passes, each one replica at a time (advisory lock or SKIP LOCKED).</summary>
    public static IServiceCollection AddSweeps(this IServiceCollection services)
    {
        services.AddHostedService<ScheduledPublishWorker>();
        // analytics.events has no other retention: the daily rollups hold the aggregate.
        services.AddHostedService<AnalyticsRetentionWorker>();
        // Invitations are a passive column nothing else sweeps.
        services.AddHostedService<InvitationExpiryWorker>();
        services.AddHostedService<ApiConnectionWorker>();
        services.AddHostedService<NotificationRetentionWorker>();
        services.AddHostedService<AiConversationRetentionWorker>();
        // The platform bell from the edge's certificate-attempt ledger (the rate-limit guard).
        services.AddHostedService<CertificateNotificationWorker>();
        // Seals finished months, keeps partitions ahead of the writer, applies retention.
        services.AddHostedService<AuditMaintenanceWorker>();
        services.AddHostedService<TenantUsageWorker>();
        return services;
    }

    /// <summary>Plugins' event handlers, jobs and interval scheduler (their hosted services come with AddDcmsAdminPlane).</summary>
    public static IServiceCollection AddPluginWork(this IServiceCollection services)
    {
        services.AddDcmsPluginWorkers();
        return services;
    }
}
