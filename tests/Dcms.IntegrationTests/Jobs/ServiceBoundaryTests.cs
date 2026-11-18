extern alias AdminApiApp;
extern alias JobsWorkerApp;
using Dcms.IntegrationTests.Tenancy;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dcms.IntegrationTests.Jobs;

/// <summary>
/// The admin plane's two services (ADR 0036): admin-api serves the console's requests and runs no
/// background work; jobs-worker runs all of it and serves nothing but health. Checked on the real
/// compositions, so a hosted service registered on the wrong side fails here, not in production.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class ServiceBoundaryTests(AdminApiFixture fixture)
{
    private static readonly string[] Background =
    [
        nameof(JobConsumerHost), nameof(JobMaintenanceWorker), nameof(JobStateIngestConsumer),
        "PluginJobConsumer", "PluginEventConsumer", "PluginJobScheduler", "AutomationWorker", "RunWorker", "SiteGateReconciler",
        "MembershipChangedConsumer", "OutboxDispatcher", "ScheduledPublishWorker", "AnalyticsRetentionWorker",
        "SitePublishedNotificationConsumer", "SiteBuildFailedNotificationConsumer", "MediaProcessedNotificationConsumer",
        "MediaFailedNotificationConsumer", "ContentPublishedNotificationConsumer", "ContentUnpublishedNotificationConsumer",
        "DomainVerifiedNotificationConsumer", "PluginInstanceNotificationConsumer", "NotificationIngestConsumer",
        "InvitationExpiryWorker", "ApiConnectionWorker", "NotificationRetentionWorker", "AiConversationRetentionWorker",
        "CertificateNotificationWorker", "AuditChainWriter", "AuditIngestConsumer", "AuditMaintenanceWorker",
        "AuditLogProjector", "TenantUsageWorker",
    ];

    [DockerFact]
    public void Admin_api_serves_requests_and_runs_no_background_work()
    {
        var services = fixture.Factory.Services;
        var ours = Hosted(services).Where(t => t.Namespace?.StartsWith("Dcms.", StringComparison.Ordinal) == true).Select(t => t.Name).ToList();

        ours.Should().NotContain(Background);
        // Everything of the platform's own it still runs: the migrator (dev only) and the socket push.
        ours.Should().BeSubsetOf(["TenancyMigrator", "JobLiveUpdates"], "admin-api serves requests; jobs-worker does the background work");
        ours.Should().Contain("JobLiveUpdates", "the replica holding the sockets pushes to them");
        services.GetRequiredService<PluginHost>().RunsBackgroundWork.Should().BeFalse("plugins register their hosted services in jobs-worker only");
    }

    [DockerFact]
    public async Task Jobs_worker_runs_the_background_work_and_serves_only_health()
    {
        // The shared fixture leaves the lanes off (the ledger tests claim jobs themselves).
        await using var worker = fixture.StartWorker(b => b.UseSetting("Jobs:Lanes:tenant:Enabled", "true"));
        var services = worker.Services;
        var ours = Hosted(services).Select(t => t.Name).ToList();

        ours.Should().Contain(Background);
        ours.Should().NotContain("JobLiveUpdates", "it holds no sockets");
        services.GetRequiredService<PluginHost>().RunsBackgroundWork.Should().BeTrue();

        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints;
        endpoints.Where(e => e.Metadata.GetMetadata<HubMetadata>() is not null).Should().BeEmpty("no hub is mapped here");
        endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText ?? "")
            .Should().OnlyContain(p => p == "/" || p.StartsWith("/health", StringComparison.Ordinal) || p.StartsWith("/metrics", StringComparison.Ordinal),
                "nothing routes to jobs-worker; it drains queues and tables");
    }

    private static IEnumerable<Type> Hosted(IServiceProvider services) =>
        services.GetServices<IHostedService>().Select(s => s.GetType());
}
