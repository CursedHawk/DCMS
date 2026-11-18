extern alias AdminApiApp;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dcms.IntegrationTests.Jobs;

/// <summary>
/// admin-api's two roles (ADR 0024): the api role serves requests and runs none of the
/// background work, which is jobs-worker's; the worker role runs it and pushes nothing to sockets.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class AdminRoleTests(AdminApiFixture fixture)
{
    private static readonly string[] Background =
    [
        nameof(JobConsumerHost), nameof(JobMaintenanceWorker), nameof(JobStateIngestConsumer),
        "PluginJobConsumer", "PluginEventConsumer", "PluginJobScheduler", "AutomationWorker", "RunWorker", "SiteGateReconciler",
        // admin-api's own (ADR 0036): these ran in both containers until 2026-11-18.
        "MembershipChangedConsumer", "OutboxDispatcher", "ScheduledPublishWorker", "AnalyticsRetentionWorker",
        "SitePublishedNotificationConsumer", "SiteBuildFailedNotificationConsumer", "MediaProcessedNotificationConsumer",
        "MediaFailedNotificationConsumer", "ContentPublishedNotificationConsumer", "ContentUnpublishedNotificationConsumer",
        "DomainVerifiedNotificationConsumer", "PluginInstanceNotificationConsumer", "NotificationIngestConsumer",
        "InvitationExpiryWorker", "ApiConnectionWorker", "NotificationRetentionWorker", "AiConversationRetentionWorker",
        "CertificateNotificationWorker", "AuditChainWriter", "AuditIngestConsumer", "AuditMaintenanceWorker",
        "AuditLogProjector", "TenantUsageWorker",
    ];

    [DockerFact]
    public async Task The_api_role_leaves_the_background_work_to_the_worker()
    {
        await using var api = fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Dcms:Role", "api");
            b.UseSetting("Jobs:Lanes:tenant:Enabled", "true");
        });
        var hosted = HostedNames(api.Services);

        hosted.Should().NotContain(Background);
        // Everything of the platform's own it still runs: the migrator (dev only) and the socket push.
        HostedTypes(api).Where(t => t.Namespace?.StartsWith("Dcms.", StringComparison.Ordinal) == true).Select(t => t.Name)
            .Should().BeSubsetOf(["TenancyMigrator", "JobLiveUpdates"], "admin-api serves requests; jobs-worker does the background work");
        // Routes run plugin code for another instance too (Functions' HTTP invoke): the runner is everywhere.
        api.Services.GetRequiredService<Dcms.PluginSdk.Runtime.Hosting.PluginHandlerRunner>().Should().NotBeNull();
        hosted.Should().Contain("JobLiveUpdates", "the replica holding the sockets pushes to them");
    }

    [DockerFact]
    public async Task The_worker_role_runs_it_and_pushes_to_no_sockets()
    {
        await using var worker = fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Dcms:Role", "worker");
            b.UseSetting("Jobs:Lanes:tenant:Enabled", "true");
        });
        var hosted = HostedNames(worker.Services);

        hosted.Should().Contain(Background);
        hosted.Should().NotContain("JobLiveUpdates");
    }

    private static List<string> HostedNames(IServiceProvider services) =>
        services.GetServices<IHostedService>().Select(s => s.GetType().Name).ToList();

    private static IEnumerable<Type> HostedTypes(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<AdminApiApp::Program> host) =>
        host.Services.GetServices<IHostedService>().Select(s => s.GetType());
}
