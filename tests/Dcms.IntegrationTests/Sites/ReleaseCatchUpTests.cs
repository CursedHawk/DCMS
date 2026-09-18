extern alias AdminApiApp;
using System.Net;
using System.Text;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Sites;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.JetStream.Models;
using NATS.Net;
using SiteEndpoints = AdminApiApp::Dcms.AdminApi.Sites.SiteEndpoints;
using ForgejoClient = AdminApiApp::Dcms.AdminApi.Sites.Git.ForgejoClient;

namespace Dcms.IntegrationTests.Sites;

/// <summary>
/// BUG-01: a push to <c>release</c> while a build is running used to be dropped for good — the
/// webhook coalesces it into the in-flight build, whose snapshot predates the push, and nothing
/// ever built the newer commit. The fix is a latest-wins catch-up at every build's terminal event.
///
/// <para>Runs the real catch-up against real Postgres + NATS (the shared admin-api containers) with
/// only Forgejo stubbed, on a derived host that has git switched on — so the shared fixture's
/// other tests keep git off.</para>
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class ReleaseCatchUpTests(AdminApiFixture fixture)
{
    private const string BuiltSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PushedSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [DockerFact]
    public async Task A_push_that_landed_mid_build_is_built_once_when_the_build_ends()
    {
        var ct = TestContext.Current.CancellationToken;
        var forgejo = new ForgejoStub { ReleaseHead = PushedSha };
        await using var host = await StartAsync(forgejo, ct);
        var (tenantId, siteId, finishedBuild) = await SeedAsync(host, builtSha: BuiltSha, ct);

        await RunCatchUpAsync(host, tenantId, siteId, finishedBuild, ct);

        var queued = await QueuedBuildsAsync(host, siteId, ct);
        queued.Should().ContainSingle("the commit pushed during the build must get a build of its own");
        queued[0].GitCommitSha.Should().Be(PushedSha);
        // Built from the tree AT the new head, not the stale snapshot the finished build had.
        queued[0].DefinitionSnapshotJson.Should().Contain("src/main.tsx");

        // The same terminal event redelivered (or a second build ending) must not stack another:
        // the catch-up is now in flight, and its own completion will re-check the head.
        await RunCatchUpAsync(host, tenantId, siteId, finishedBuild, ct);
        (await QueuedBuildsAsync(host, siteId, ct)).Should().ContainSingle();
    }

    [DockerFact]
    public async Task Nothing_is_queued_when_the_release_head_is_what_was_just_built()
    {
        var ct = TestContext.Current.CancellationToken;
        var forgejo = new ForgejoStub { ReleaseHead = BuiltSha };
        await using var host = await StartAsync(forgejo, ct);
        var (tenantId, siteId, finishedBuild) = await SeedAsync(host, builtSha: BuiltSha, ct);

        await RunCatchUpAsync(host, tenantId, siteId, finishedBuild, ct);

        (await QueuedBuildsAsync(host, siteId, ct)).Should().BeEmpty();
    }

    private async Task<WebApplicationFactory<AdminApiApp::Program>> StartAsync(ForgejoStub forgejo, CancellationToken ct)
    {
        var host = fixture.Factory.WithWebHostBuilder(builder =>
        {
            // A machine token is what switches git on (ForgejoOptions.Enabled).
            builder.UseSetting("Forgejo:Token", "test-token");
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<ForgejoClient>().ConfigurePrimaryHttpMessageHandler(() => forgejo));
        });

        // The catch-up enqueues onto the SITES work queue, which the deploy's provisioning script
        // creates (infra/nats/provision-streams.sh) and the shared fixture does not.
        var natsUrl = host.Services.GetRequiredService<IConfiguration>()["Nats:Url"];
        await using var nats = new NatsClient(natsUrl!);
        await nats.CreateJetStreamContext().CreateOrUpdateStreamAsync(
            new StreamConfig(Streams.Sites, ["site.publish.>"]) { Retention = StreamConfigRetention.Workqueue }, ct);

        return host;
    }

    private static async Task<(Guid TenantId, Guid SiteId, Guid FinishedBuild)> SeedAsync(
        WebApplicationFactory<AdminApiApp::Program> host, string builtSha, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SitesDbContext>();

        var tenantId = Guid.NewGuid();
        var site = new Site
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = "catch-up",
            RenderMode = SiteRenderMode.ReactApp, GitRepoFullName = $"tenant-x/site-{Guid.NewGuid():N}",
        };
        var finished = new SiteBuild
        {
            Id = Guid.NewGuid(), TenantId = tenantId, SiteId = site.Id, Status = SiteBuildStatus.Succeeded,
            GitCommitSha = builtSha, CompletedAt = DateTimeOffset.UtcNow,
        };
        site.ActiveBuildId = finished.Id;
        db.Sites.Add(site);
        db.Builds.Add(finished);
        await db.SaveChangesAsync(ct);
        return (tenantId, site.Id, finished.Id);
    }

    /// <summary>What the site notification consumers run at a build's terminal event.</summary>
    private static async Task RunCatchUpAsync(
        WebApplicationFactory<AdminApiApp::Program> host, Guid tenantId, Guid siteId, Guid finishedBuild, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        await SiteEndpoints.ContinueIfReleaseMovedAsync(scope.ServiceProvider, tenantId, siteId, finishedBuild, ct);
    }

    private static async Task<List<SiteBuild>> QueuedBuildsAsync(
        WebApplicationFactory<AdminApiApp::Program> host, Guid siteId, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SitesDbContext>();
        return await db.Builds.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.SiteId == siteId && b.Status == SiteBuildStatus.Queued)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The three Forgejo reads the catch-up makes: the release head, and the tree + blob it
    /// snapshots. Anything else is a 404 so an unexpected call cannot quietly succeed.
    /// </summary>
    private sealed class ForgejoStub : HttpMessageHandler
    {
        public required string ReleaseHead { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object? body =
                path.EndsWith("/branches/release", StringComparison.Ordinal)
                    ? new { name = "release", commit = new { id = ReleaseHead } }
                : path.Contains("/git/trees/", StringComparison.Ordinal)
                    ? new { tree = new[] { new { path = "src/main.tsx", type = "blob", sha = "blob1" } }, truncated = false }
                : path.EndsWith("/git/blobs/blob1", StringComparison.Ordinal)
                    ? new { content = Convert.ToBase64String(Encoding.UTF8.GetBytes("export {};")), encoding = "base64" }
                : null;

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                });
        }
    }
}
