extern alias AdminApiApp;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Sites;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.JetStream.Models;
using NATS.Net;
using ForgejoClient = AdminApiApp::Dcms.AdminApi.Sites.Git.ForgejoClient;

namespace Dcms.IntegrationTests.Sites;

/// <summary>
/// #277: a branch besides release can publish, to its own domains. Pushes to it build onto it,
/// never onto release; its domains are never the site's primary; and it stops cleanly.
/// Real Postgres + NATS, Forgejo stubbed (as <see cref="ReleaseCatchUpTests"/>).
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class PublishBranchTests(AdminApiFixture fixture)
{
    private const string Secret = "test-webhook-secret";
    private const string DevHead = "dddddddddddddddddddddddddddddddddddddddd";

    [DockerFact]
    public async Task A_publishing_branch_builds_on_push_and_serves_only_its_own_domains()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        var client = host.CreateClient();
        var (slug, tenantId) = await CreateTenantAsync(client, ct);
        var repo = $"tenant-x/site-{Guid.NewGuid():N}";
        var siteId = await SeedSiteAsync(host, tenantId, repo, ct);

        // Only a branch the repository has, and never release itself.
        (await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/sites/{siteId}/publish-branches", slug, new { branch = "nope" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/sites/{siteId}/publish-branches", slug, new { branch = "release" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var added = await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/sites/{siteId}/publish-branches", slug, new { branch = "dev" }), ct);
        added.StatusCode.Should().Be(HttpStatusCode.Created, await added.Content.ReadAsStringAsync(ct));

        // Adding it builds its head at once, as the branch's build.
        var first = (await BuildsAsync(host, siteId, ct)).Should().ContainSingle().Subject;
        first.Branch.Should().Be("dev");
        first.GitCommitSha.Should().Be(DevHead);

        // A push to it while that build runs is folded into it; a push to a branch that does not
        // publish builds nothing.
        (await PushAsync(client, repo, "dev", ct)).Should().Contain("build in flight");
        (await PushAsync(client, repo, "feature", ct)).Should().Contain("not a publishing branch");
        await FinishAsync(host, first.Id, ct);
        (await PushAsync(client, repo, "dev", ct)).Should().Contain("buildId");
        (await BuildsAsync(host, siteId, ct)).Where(b => b.Status == SiteBuildStatus.Queued)
            .Should().ContainSingle().Which.Branch.Should().Be("dev");

        // Going live on the branch moves the branch's pointer, not the site's.
        (await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/sites/{siteId}/builds/{first.Id}/activate", slug, null), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        using (var scope = host.Services.CreateScope())
        using (RlsScope.Tenant(tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<SitesDbContext>();
            (await db.Sites.IgnoreQueryFilters().SingleAsync(s => s.Id == siteId, ct)).ActiveBuildId.Should().BeNull();
            (await db.PublishBranches.IgnoreQueryFilters().SingleAsync(p => p.SiteId == siteId, ct)).ActiveBuildId.Should().Be(first.Id);
        }

        // A domain on the branch: linked to it, never the site's primary.
        var domainId = await ProvisionDomainAsync(client, slug, ct);
        (await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/domains/{domainId}/site", slug, new { siteId, branch = "other" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/domains/{domainId}/site", slug, new { siteId, branch = "dev" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/domains/{domainId}/primary", slug, null), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var branches = await (await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/sites/{siteId}/publish-branches", slug, null), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);
        branches.EnumerateArray().Select(b => b.GetProperty("branch").GetString()).Should().Equal("release", "dev");
        branches[0].GetProperty("domains").GetArrayLength().Should().Be(0);
        var devDomain = branches[1].GetProperty("domains").EnumerateArray().Should().ContainSingle().Subject;
        devDomain.GetProperty("primary").GetBoolean().Should().BeFalse();

        // Stopping it unlinks its domains and stops its pushes building.
        (await client.SendAsync(Req(HttpMethod.Delete, $"/api/admin/sites/{siteId}/publish-branches/dev", slug, null), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var domains = await (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/domains", slug, null), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);
        var unlinked = domains.EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == domainId);
        unlinked.GetProperty("siteId").ValueKind.Should().Be(JsonValueKind.Null);
        unlinked.GetProperty("branch").ValueKind.Should().Be(JsonValueKind.Null);
        (await PushAsync(client, repo, "dev", ct)).Should().Contain("not a publishing branch");
    }

    [DockerFact]
    public async Task A_push_builds_the_site_in_its_own_environment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        var client = host.CreateClient();
        var (_, tenantId) = await CreateTenantAsync(client, ct);
        var repo = $"tenant-x/site-{Guid.NewGuid():N}";
        var environmentId = Guid.NewGuid();
        var siteId = await SeedSiteAsync(host, tenantId, repo, ct, environmentId);

        (await PushAsync(client, repo, "release", ct)).Should().Contain("buildId");
        (await BuildsAsync(host, siteId, ct)).Should().ContainSingle()
            .Which.EnvironmentId.Should().Be(environmentId, "the webhook names no environment; the site does");
    }

    private async Task<WebApplicationFactory<AdminApiApp::Program>> StartAsync(CancellationToken ct)
    {
        var host = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Forgejo:Token", "test-token");
            builder.UseSetting("Forgejo:WebhookSecret", Secret);
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<ForgejoClient>().ConfigurePrimaryHttpMessageHandler(() => new ForgejoStub()));
        });
        var natsUrl = host.Services.GetRequiredService<IConfiguration>()["Nats:Url"];
        await using var nats = new NatsClient(natsUrl!);
        await nats.CreateJetStreamContext().CreateOrUpdateStreamAsync(
            new StreamConfig(Streams.Sites, ["site.publish.>"]) { Retention = StreamConfigRetention.Workqueue }, ct);
        return host;
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, string slug, object? body)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", Guid.NewGuid().ToString());
        req.Headers.Add("X-Test-Roles", "SuperAdmin");
        req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<(string Slug, Guid TenantId)> CreateTenantAsync(HttpClient client, CancellationToken ct)
    {
        var slug = "pb-" + Guid.NewGuid().ToString("N")[..8];
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new { slug, name = slug, ownerUserId = Guid.NewGuid(), ownerEmail = $"{slug}@dcms.test" }),
        };
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return (slug, (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid());
    }

    private static async Task<Guid> SeedSiteAsync(
        WebApplicationFactory<AdminApiApp::Program> host, Guid tenantId, string repo, CancellationToken ct, Guid? environmentId = null)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SitesDbContext>();
        using var rls = RlsScope.Environment(tenantId, environmentId ?? tenantId);
        var site = new Site
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = "branches",
            RenderMode = SiteRenderMode.ReactApp, GitRepoFullName = repo, GitDefaultBranch = "main",
        };
        db.Sites.Add(site);
        await db.SaveChangesAsync(ct);
        return site.Id;
    }

    private static async Task<Guid> ProvisionDomainAsync(HttpClient client, string slug, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/domains/provisioned", slug, null), ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    /// <summary>What Forgejo posts on a push: signed with the shared secret.</summary>
    private static async Task<string> PushAsync(HttpClient client, string repo, string branch, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            @ref = $"refs/heads/{branch}",
            after = DevHead,
            repository = new { full_name = repo },
        });
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/internal/git/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Gitea-Signature", Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
        var res = await client.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return text;
    }

    private static async Task<List<SiteBuild>> BuildsAsync(
        WebApplicationFactory<AdminApiApp::Program> host, Guid siteId, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        using var rls = RlsScope.Platform();
        return await scope.ServiceProvider.GetRequiredService<SitesDbContext>().Builds.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.SiteId == siteId).OrderBy(b => b.CreatedAt).ToListAsync(ct);
    }

    /// <summary>What site-builder does at the end of a build, minus going live.</summary>
    private static async Task FinishAsync(WebApplicationFactory<AdminApiApp::Program> host, Guid buildId, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        using var rls = RlsScope.Platform();
        await scope.ServiceProvider.GetRequiredService<SitesDbContext>().Builds.IgnoreQueryFilters()
            .Where(b => b.Id == buildId)
            .ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, SiteBuildStatus.Succeeded)
                .SetProperty(b => b.CompletedAt, DateTimeOffset.UtcNow), ct);
    }

    /// <summary>A repository with release and dev; any other branch does not exist.</summary>
    private sealed class ForgejoStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object? body =
                path.EndsWith("/branches/dev", StringComparison.Ordinal)
                    ? new { name = "dev", commit = new { id = DevHead } }
                : path.EndsWith("/branches/release", StringComparison.Ordinal)
                    ? new { name = "release", commit = new { id = "rrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrr" } }
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
