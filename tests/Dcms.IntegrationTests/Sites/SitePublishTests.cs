extern alias SiteHostApp;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.TestHost;
using SitemapEndpoints = SiteHostApp::Dcms.SiteHost.SitemapEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Sites;

[Collection(SitePublishCollection.Name)]
public class SitePublishTests(SitePublishFixture fixture)
{
    private static readonly Guid Owner = Guid.NewGuid();

    [DockerFact]
    public async Task Published_site_is_served_on_its_verified_domain_and_isolated_from_others()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fixture.Admin.CreateClient();
        var hostClient = fixture.Host.CreateClient();
        var slug = "site-" + Guid.NewGuid().ToString("N")[..8];
        var hostname = $"{slug}.example.com";

        // Tenant + site with a one-page source tree, in the Mode A file-map format
        // the visual builder commits (site.json + pages/*.html + styles/*.css).
        var tenantId = await Id(admin, Admin(HttpMethod.Post, "/api/admin/tenants", slug,
            new { slug, name = slug, ownerUserId = Owner, ownerEmail = "o@dcms.test" }, superAdmin: true), ct);

        var manifest = """
            {
              "version": 2,
              "theme": { "colors": {}, "fonts": {} },
              "nav": [],
              "pages": [
                { "id": "home", "slug": "home", "path": "/", "title": "Home", "home": true,
                  "seo": { "title": "Hello Site" } }
              ]
            }
            """;
        var definition = new
        {
            files = new Dictionary<string, string>
            {
                ["site.json"] = manifest,
                ["pages/home.html"] = "<section class=\"hero\"><h1>Welcome Visitor</h1></section>",
                ["styles/global.css"] = "body{margin:0}",
                ["styles/pages/home.css"] = ".hero{padding:2rem}",
            },
        };
        var siteId = await Id(admin, Admin(HttpMethod.Post, "/api/admin/sites", slug,
            new { name = "Main", renderMode = "StaticPrerender", definition = JsonSerializer.Serialize(definition) }), ct);

        // Domain: add → verify (AutoVerify) → link to the site.
        var domainId = await Id(admin, Admin(HttpMethod.Post, "/api/admin/domains", slug, new { hostname }), ct);
        (await admin.SendAsync(Admin(HttpMethod.Post, $"/api/admin/domains/{domainId}/verify", slug, null), ct))
            .EnsureSuccessStatusCode();
        (await admin.SendAsync(Admin(HttpMethod.Post, $"/api/admin/domains/{domainId}/site", slug, new { siteId }), ct))
            .EnsureSuccessStatusCode();

        // Publish → site-builder renders → site-host serves.
        var publish = await admin.SendAsync(Admin(HttpMethod.Post, $"/api/admin/sites/{siteId}/publish", slug, null), ct);
        publish.EnsureSuccessStatusCode();
        var buildId = (await publish.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("buildId").GetGuid();

        await PollUntil(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/");
            req.Headers.Host = hostname;
            var res = await hostClient.SendAsync(req, ct);
            if (res.StatusCode != HttpStatusCode.OK) return false;
            var html = await res.Content.ReadAsStringAsync(ct);
            return html.Contains("Welcome Visitor");
        }, TimeSpan.FromSeconds(30));

        // The build was a job on the ledger under its own id, and site-builder's reports over the
        // bus carried it through to Succeeded (ADR 0024).
        await PollUntil(async () =>
        {
            var res = await admin.SendAsync(Admin(HttpMethod.Get, $"/api/admin/jobs/{buildId}", slug, null), ct);
            if (!res.IsSuccessStatusCode) return false;
            var job = (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("job");
            return job.GetProperty("kind").GetString() == "site.build" && job.GetProperty("status").GetString() == "Succeeded";
        }, TimeSpan.FromSeconds(30));

        // Isolation: an unknown domain serves nothing.
        var foreign = new HttpRequestMessage(HttpMethod.Get, "/");
        foreign.Headers.Host = "unknown.example.com";
        (await hostClient.SendAsync(foreign, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The edge's certificate gate reads every tenant's domains (RlsScope.Platform); under the
        // app role a missing scope would refuse every certificate rather than leak anything.
        (await hostClient.GetAsync($"/internal/tls-allowed?domain={hostname}", ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await hostClient.GetAsync("/internal/tls-allowed?domain=unknown.example.com", ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await hostClient.GetFromJsonAsync<string[]>("/internal/tls-hostnames", ct))
            .Should().Contain(hostname);

        // The site's own /api is content-api's, reached straight from the edge with nothing but the
        // Host: content-api resolves it with the same DomainResolver site-host uses (ADR 0010).
        var content = fixture.Content.CreateClient();
        HttpRequestMessage Api(string host)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/openapi.json");
            req.Headers.Host = host;
            return req;
        }
        (await content.SendAsync(Api(hostname), ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await content.SendAsync(Api("unknown.example.com"), ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Suspension reaches the public site through TenantStatusInvalidator, which reads the
        // tenant's domains as that tenant. If it read nothing, the route would stay cached for
        // its five-minute TTL and the suspended site would keep serving well past this poll.
        (await admin.SendAsync(Admin(HttpMethod.Post, $"/api/admin/tenants/{tenantId}/suspend", slug, null, superAdmin: true), ct))
            .EnsureSuccessStatusCode();
        await PollUntil(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/");
            req.Headers.Host = hostname;
            return (await hostClient.SendAsync(req, ct)).StatusCode == HttpStatusCode.NotFound;
        }, TimeSpan.FromSeconds(15));
        // And its API, through content-api's own copy of the same invalidator.
        await PollUntil(async () => (await content.SendAsync(Api(hostname), ct)).StatusCode == HttpStatusCode.NotFound,
            TimeSpan.FromSeconds(15));
    }

    [DockerFact]
    public async Task A_publishing_branch_domain_serves_the_branch_build_and_is_kept_out_of_search()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fixture.Admin.CreateClient();
        var hostClient = fixture.Host.CreateClient();
        var slug = "branch-" + Guid.NewGuid().ToString("N")[..8];
        var tenantId = await Id(admin, Admin(HttpMethod.Post, "/api/admin/tenants", slug,
            new { slug, name = slug, ownerUserId = Owner, ownerEmail = "o@dcms.test" }, superAdmin: true), ct);

        string Definition(string heading) => JsonSerializer.Serialize(new
        {
            files = new Dictionary<string, string>
            {
                ["site.json"] = """{ "version": 2, "theme": { "colors": {}, "fonts": {} }, "nav": [], "pages": [ { "id": "home", "slug": "home", "path": "/", "title": "Home", "home": true } ] }""",
                ["pages/home.html"] = $"<h1>{heading}</h1>",
                ["styles/global.css"] = "",
            },
        });
        var siteId = await Id(admin, Admin(HttpMethod.Post, "/api/admin/sites", slug,
            new { name = "Main", renderMode = "StaticPrerender", definition = Definition("Released") }), ct);

        var release = $"{slug}.example.com";
        var dev = $"dev.{slug}.example.com";
        foreach (var hostname in new[] { release, dev })
        {
            var domainId = await Id(admin, Admin(HttpMethod.Post, "/api/admin/domains", slug, new { hostname }), ct);
            (await admin.SendAsync(Admin(HttpMethod.Post, $"/api/admin/domains/{domainId}/verify", slug, null), ct)).EnsureSuccessStatusCode();
            (await admin.SendAsync(Admin(HttpMethod.Post, $"/api/admin/domains/{domainId}/site", slug, new { siteId }), ct)).EnsureSuccessStatusCode();
        }

        // Two builds: the released one, then the one the branch will serve.
        async Task<Guid> PublishAsync()
        {
            var res = await admin.SendAsync(Admin(HttpMethod.Post, $"/api/admin/sites/{siteId}/publish", slug, null), ct);
            res.EnsureSuccessStatusCode();
            var buildId = (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("buildId").GetGuid();
            await PollUntil(async () =>
            {
                using var scope = fixture.Admin.Services.CreateScope();
                using var rls = Dcms.Shared.Data.Rls.RlsScope.Tenant(tenantId);
                var db = scope.ServiceProvider.GetRequiredService<Dcms.Shared.Data.Sites.SitesDbContext>();
                return await db.Builds.IgnoreQueryFilters().AnyAsync(
                    b => b.Id == buildId && b.Status == Dcms.Shared.Data.Sites.SiteBuildStatus.Succeeded, ct);
            }, TimeSpan.FromSeconds(30));
            return buildId;
        }
        var released = await PublishAsync();
        (await admin.SendAsync(Admin(HttpMethod.Put, $"/api/admin/sites/{siteId}/definition", slug,
            JsonDocument.Parse(Definition("On dev")).RootElement), ct)).EnsureSuccessStatusCode();
        var devBuild = await PublishAsync();

        // The branch and its domain, as the publish-branch endpoints leave them (they need git,
        // which this fixture does not run), with release put back on the first build.
        using (var scope = fixture.Admin.Services.CreateScope())
        using (Dcms.Shared.Data.Rls.RlsScope.Tenant(tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<Dcms.Shared.Data.Sites.SitesDbContext>();
            db.PublishBranches.Add(new Dcms.Shared.Data.Sites.SitePublishBranch
            {
                Id = Guid.NewGuid(), TenantId = tenantId, SiteId = siteId, Branch = "dev", ActiveBuildId = devBuild,
            });
            var site = await db.Sites.IgnoreQueryFilters().SingleAsync(s => s.Id == siteId, ct);
            site.ActiveBuildId = released;
            await db.SaveChangesAsync(ct);
            var tenancy = scope.ServiceProvider.GetRequiredService<Dcms.Shared.Data.Tenancy.TenancyDbContext>();
            await tenancy.Domains.IgnoreQueryFilters().Where(d => d.Hostname == dev)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.Branch, "dev"), ct);
        }

        async Task<HttpResponseMessage> GetAsync(string hostname)
        {
            fixture.Host.Services.GetRequiredService<Dcms.Shared.Sites.DomainResolver>().Invalidate(hostname);
            var req = new HttpRequestMessage(HttpMethod.Get, "/");
            req.Headers.Host = hostname;
            return await hostClient.SendAsync(req, ct);
        }
        var onRelease = await GetAsync(release);
        (await onRelease.Content.ReadAsStringAsync(ct)).Should().Contain("Released");
        onRelease.Headers.Contains("X-Robots-Tag").Should().BeFalse();

        var onDev = await GetAsync(dev);
        (await onDev.Content.ReadAsStringAsync(ct)).Should().Contain("On dev");
        onDev.Headers.GetValues("X-Robots-Tag").Should().ContainSingle().Which.Should().Contain("noindex");
    }

    [DockerFact]
    public async Task A_published_site_has_a_sitemap_of_its_pages_and_published_items_and_a_robots_txt_naming_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fixture.Admin.CreateClient();
        var slug = "map-" + Guid.NewGuid().ToString("N")[..8];
        var hostname = $"{slug}.example.com";
        await Id(admin, Admin(HttpMethod.Post, "/api/admin/tenants", slug,
            new { slug, name = slug, ownerUserId = Owner, ownerEmail = "o@dcms.test" }, superAdmin: true), ct);

        var events = await Id(admin, Admin(HttpMethod.Post, "/api/admin/plugins/instances", slug,
            new { pluginId = "events", slug = "events", name = "Events", config = "{}" }), ct);
        foreach (var (item, published) in new[] { ("opening", true), ("closing", true), ("draft-only", false) })
        {
            var id = await Id(admin, Admin(HttpMethod.Post, "/api/admin/content", slug, new
            {
                pluginInstanceId = events, contentType = "event", slug = item,
                data = new { title = item, date = "2026-12-01T20:00:00Z", venue = "Roxy" },
            }), ct);
            if (published)
            {
                (await admin.SendAsync(Admin(HttpMethod.Post, $"/api/admin/content/{id}/publish", slug, null), ct)).EnsureSuccessStatusCode();
            }
        }

        var definition = new
        {
            files = new Dictionary<string, string>
            {
                ["site.json"] = """
                    { "version": 2, "theme": { "colors": {}, "fonts": {} }, "nav": [], "pages": [
                      { "id": "home", "slug": "home", "path": "/", "title": "Home", "home": true, "seo": { "title": "Home" } },
                      { "id": "about", "slug": "about", "path": "/about", "title": "About", "seo": { "title": "About" } },
                      { "id": "draft", "slug": "draft", "path": "/draft", "title": "Draft", "seo": { "title": "Draft", "noIndex": true } },
                      { "id": "event", "slug": "event", "path": "/events/:slug", "title": "Event", "seo": { "title": "Event" } } ] }
                    """,
                ["pages/home.html"] = "<h1>Mapped</h1>",
                ["pages/about.html"] = "<p>About</p>",
                ["pages/draft.html"] = "<p>Draft</p>",
                ["pages/event.html"] = """<div data-dcms-component="Events.Event" data-dcms-bindings='[{"propPath":"item","instanceSlug":"events","query":{"contentType":"event"}}]'></div>""",
            },
        };
        var siteId = await Id(admin, Admin(HttpMethod.Post, "/api/admin/sites", slug,
            new { name = "Main", renderMode = "StaticPrerender", definition = JsonSerializer.Serialize(definition) }), ct);
        var domainId = await Id(admin, Admin(HttpMethod.Post, "/api/admin/domains", slug, new { hostname }), ct);
        (await admin.SendAsync(Admin(HttpMethod.Post, $"/api/admin/domains/{domainId}/verify", slug, null), ct)).EnsureSuccessStatusCode();
        (await admin.SendAsync(Admin(HttpMethod.Post, $"/api/admin/domains/{domainId}/site", slug, new { siteId }), ct)).EnsureSuccessStatusCode();
        (await admin.SendAsync(Admin(HttpMethod.Post, $"/api/admin/sites/{siteId}/publish", slug, null), ct)).EnsureSuccessStatusCode();

        // site-host lists items through content-api: here, the in-memory one.
        await using var hostFactory = fixture.Host.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient(SitemapEndpoints.ContentApiClient).ConfigurePrimaryHttpMessageHandler(() => fixture.Content.Server.CreateHandler())));
        var host = hostFactory.CreateClient();
        async Task<HttpResponseMessage> GetAsync(string path)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Host = hostname;
            return await host.SendAsync(req, ct);
        }
        await PollUntil(async () => (await GetAsync("/")).StatusCode == HttpStatusCode.OK, TimeSpan.FromSeconds(30));

        var sitemap = await GetAsync("/sitemap.xml");
        sitemap.StatusCode.Should().Be(HttpStatusCode.OK, await sitemap.Content.ReadAsStringAsync(ct));
        sitemap.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var locs = XDocument.Parse(await sitemap.Content.ReadAsStringAsync(ct)).Root!
            .Elements(ns + "url").Select(u => u.Element(ns + "loc")!.Value).ToList();
        locs.Should().BeEquivalentTo(
            [$"http://{hostname}/", $"http://{hostname}/about", $"http://{hostname}/events/opening", $"http://{hostname}/events/closing"],
            "noindex pages and unpublished items are left out");

        (await (await GetAsync("/robots.txt")).Content.ReadAsStringAsync(ct)).Should().Contain($"Sitemap: http://{hostname}/sitemap.xml");
        (await GetAsync("/_dcms/pages.json")).StatusCode.Should().Be(HttpStatusCode.NotFound, "it names gated pages too");

        // Its people's say (#648): what the console shows, then hiding a page and an item, adding a URL.
        var view = await (await admin.SendAsync(Admin(HttpMethod.Get, $"/api/admin/sites/{siteId}/search", slug, null), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);
        view.GetProperty("settings").GetProperty("indexable").GetBoolean().Should().BeTrue();
        view.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("path").GetString())
            .Should().Equal("/", "/about", "/events/:slug");
        view.GetProperty("sitemapUrl").GetString().Should().Be($"https://{hostname}/sitemap.xml");

        (await admin.SendAsync(Admin(HttpMethod.Put, $"/api/admin/sites/{siteId}/search", slug,
            new { indexable = true, exclude = new[] { "about" }, include = Array.Empty<string>() }), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a rule is an absolute path");
        (await admin.SendAsync(Admin(HttpMethod.Put, $"/api/admin/sites/{siteId}/search", slug,
            new { indexable = true, exclude = new[] { "/about", "/events/closing" }, include = new[] { "/landing" } }), ct))
            .EnsureSuccessStatusCode();

        async Task<List<string>> LocsAsync() => XDocument.Parse(await (await GetAsync("/sitemap.xml")).Content.ReadAsStringAsync(ct)).Root!
            .Elements(ns + "url").Select(u => u.Element(ns + "loc")!.Value).ToList();
        // No rebuild: site.settings.changed drops site-host's route, and the next request reads the rules.
        await PollUntil(async () => (await GetAsync("/about")).Headers.TryGetValues("X-Robots-Tag", out var tag)
            && tag.Single() == "noindex", TimeSpan.FromSeconds(15));
        (await GetAsync("/")).Headers.Contains("X-Robots-Tag").Should().BeFalse();
        (await LocsAsync()).Should().BeEquivalentTo(
            [$"http://{hostname}/", $"http://{hostname}/events/opening", $"http://{hostname}/landing"]);

        // The whole site hidden: every page noindex, no sitemap, robots.txt shuts the door.
        (await admin.SendAsync(Admin(HttpMethod.Put, $"/api/admin/sites/{siteId}/search", slug,
            new { indexable = false, exclude = Array.Empty<string>(), include = Array.Empty<string>() }), ct))
            .EnsureSuccessStatusCode();
        await PollUntil(async () => (await GetAsync("/sitemap.xml")).StatusCode == HttpStatusCode.NotFound, TimeSpan.FromSeconds(15));
        (await GetAsync("/")).Headers.GetValues("X-Robots-Tag").Single().Should().Be("noindex, nofollow");
        (await (await GetAsync("/robots.txt")).Content.ReadAsStringAsync(ct)).Should().Contain("Disallow: /");
    }

    private static HttpRequestMessage Admin(HttpMethod method, string url, string slug, object? body, bool superAdmin = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", Owner.ToString());
        req.Headers.Add("X-Test-Email", "o@dcms.test");
        if (superAdmin) req.Headers.Add("X-Test-Roles", "SuperAdmin");
        req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<Guid> Id(HttpClient client, HttpRequestMessage req, CancellationToken ct)
    {
        var res = await client.SendAsync(req, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);
        res.IsSuccessStatusCode.Should().BeTrue(
            $"request to {req.RequestUri} should succeed but was {res.StatusCode}: {raw}");
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.TryGetProperty("id", out var id) ? id.GetGuid()
            : json.TryGetProperty("tenantId", out var tenant) ? tenant.GetGuid()
            : json.GetProperty("siteId").GetGuid();
    }

    private static async Task PollUntil(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(500);
        }
        throw new TimeoutException("Site-host did not reach the expected state in time.");
    }
}
