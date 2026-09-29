using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// Analytics as a plugin that owns its whole path: the site beacon (content-api), the ingest
/// consumer and dashboard (admin-api), and analytics.tracking@1 for everyone else.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class PluginOwnedRoutesTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task A_page_view_reaches_the_dashboard_and_the_contract_summary()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.NewGuid();
        var tenant = "stats-" + Guid.NewGuid().ToString("N")[..8];
        var admin = fixture.Admin.CreateClient();
        var content = fixture.Content.CreateClient();
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug = tenant, name = tenant, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var beacon = () => Site(tenant, "/api/stats/collect", new { type = "pageview", path = "/pricing?utm_source=news", sessionId = "s1" });
        (await content.SendAsync(beacon(), ct)).StatusCode.Should().Be(HttpStatusCode.NotFound, "no analytics instance yet");

        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            body: new { pluginId = "analytics", slug = "stats", name = "Stats", config = "{}" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await content.SendAsync(beacon(), ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await content.SendAsync(Site(tenant, "/api/collect", new { path = "/" }), ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        JsonElement dashboard = default;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var res = await admin.SendAsync(Req(HttpMethod.Get, "/api/admin/analytics?days=1", owner, tenant), ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            dashboard = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (dashboard.GetRawText().Contains("/pricing", StringComparison.Ordinal))
            {
                break;
            }
            await Task.Delay(250, ct);
        }
        dashboard.GetRawText().Should().Contain("/pricing");

        var summary = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/contracts/analytics.tracking@1/Summary?plane=admin",
            owner, tenant, body: new { days = 1 }), ct);
        summary.StatusCode.Should().Be(HttpStatusCode.OK, await summary.Content.ReadAsStringAsync(ct));
        var body = await summary.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("pageviews").GetInt64().Should().Be(2);
        body.GetProperty("topPages").EnumerateArray().Select(p => p.GetProperty("path").GetString())
            .Should().Contain("/pricing?utm_source=news");
    }

    [DockerFact]
    public async Task Branding_and_form_review_are_served_by_their_plugins()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.NewGuid();
        var tenant = "owned-" + Guid.NewGuid().ToString("N")[..8];
        var admin = fixture.Admin.CreateClient();
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug = tenant, name = tenant, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant, body: new
        {
            pluginId = "branding", slug = "brand", name = "Brand",
            config = """{"public":{"name":"Acme","logo":"not-a-guid"},"private":{"items":[{"key":"crm","value":"secret"}]}}""",
        }), ct)).StatusCode.Should().Be(HttpStatusCode.Created);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/brand/branding");
        req.Headers.Add("X-Dcms-Tenant", tenant);
        var res = await fixture.Content.CreateClient().SendAsync(req, ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await res.Content.ReadAsStringAsync(ct);
        text.Should().Contain("\"name\":\"Acme\"").And.Contain("\"logoUrl\":null").And.NotContain("secret");

        (await admin.SendAsync(Req(HttpMethod.Get, "/api/admin/forms", owner, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static HttpRequestMessage Site(string tenant, string url, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Dcms-Tenant", tenant);
        return req;
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string slug, string roles = "", object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (roles.Length > 0) req.Headers.Add("X-Test-Roles", roles);
        if (slug.Length > 0) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }
}
