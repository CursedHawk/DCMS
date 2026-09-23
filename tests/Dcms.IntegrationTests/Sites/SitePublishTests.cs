using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
        (await admin.SendAsync(Admin(HttpMethod.Post, $"/api/admin/sites/{siteId}/publish", slug, null), ct))
            .EnsureSuccessStatusCode();

        await PollUntil(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/");
            req.Headers.Host = hostname;
            var res = await hostClient.SendAsync(req, ct);
            if (res.StatusCode != HttpStatusCode.OK) return false;
            var html = await res.Content.ReadAsStringAsync(ct);
            return html.Contains("Welcome Visitor");
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
