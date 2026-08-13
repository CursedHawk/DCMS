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

        // Tenant + site with a one-page definition.
        (await admin.SendAsync(Admin(HttpMethod.Post, "/api/admin/tenants", slug,
            new { slug, name = slug, ownerUserId = Owner, ownerEmail = "o@dcms.test" }, superAdmin: true), ct))
            .EnsureSuccessStatusCode();

        var definition = new
        {
            version = 1,
            theme = new { colors = new { }, fonts = new { } },
            nav = Array.Empty<object>(),
            pages = new[]
            {
                new
                {
                    id = "home", path = "/", title = "Home",
                    seo = new { title = "Hello Site" },
                    root = new
                    {
                        id = "h", type = "Hero",
                        props = new { title = "Welcome Visitor" },
                        bindings = Array.Empty<object>(),
                        children = Array.Empty<object>(),
                    },
                },
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
        return json.TryGetProperty("id", out var id) ? id.GetGuid() : json.GetProperty("siteId").GetGuid();
    }

    private static async Task PollUntil(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(500);
        }
        throw new TimeoutException("Site was not served in time.");
    }
}
