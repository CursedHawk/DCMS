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
}
