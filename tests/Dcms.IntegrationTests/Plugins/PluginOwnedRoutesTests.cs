using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Messaging;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// A tenant's sites are told apart (site-host stamps X-Dcms-Site), crawlers are not stored,
    /// and a batch delivered twice — committed, then its ack lost — is stored and counted once.
    /// </summary>
    [DockerFact]
    public async Task Hits_are_per_site_bots_are_dropped_and_a_redelivered_batch_counts_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.NewGuid();
        var tenant = "sites-" + Guid.NewGuid().ToString("N")[..8];
        var admin = fixture.Admin.CreateClient();
        var content = fixture.Content.CreateClient();
        var created = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug = tenant, name = tenant, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenantId = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid();
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            body: new { pluginId = "analytics", slug = "stats", name = "Stats", config = "{}" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        Guid shop = Guid.NewGuid(), blog = Guid.NewGuid();
        HttpRequestMessage Hit(Guid site, string path, string? userAgent = null, string? referrer = null)
        {
            var req = Site(tenant, "/api/collect", new { path, sessionId = "s-" + path, referrer });
            req.Headers.Add("X-Dcms-Site", site.ToString());
            req.Headers.Add("X-Dcms-Site-Host", "shop.example");
            if (userAgent is not null) req.Headers.UserAgent.ParseAdd(userAgent);
            return req;
        }
        (await content.SendAsync(Hit(shop, "/cart", referrer: "https://shop.example/"), ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await content.SendAsync(Hit(blog, "/post"), ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await content.SendAsync(Hit(shop, "/crawled", userAgent: "Mozilla/5.0 (compatible; Googlebot/2.1)"), ct))
            .StatusCode.Should().Be(HttpStatusCode.Accepted, "a crawler is told nothing, it is just not stored");

        // The same batch twice, without a message id so JetStream's own dedupe cannot hide it.
        var batch = new AnalyticsEventBatch(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId,
            [new AnalyticsEvent(DateTimeOffset.UtcNow, "pageview", "/twice", null, "s-twice", null, null, SiteId: shop)]);
        var publisher = fixture.Admin.Services.GetRequiredService<IEventPublisher>();
        await publisher.PublishAsync(Subjects.AnalyticsEvents, batch, ct);
        await publisher.PublishAsync(Subjects.AnalyticsEvents, batch, ct);

        async Task<JsonElement> Dashboard(string query)
        {
            var res = await admin.SendAsync(Req(HttpMethod.Get, $"/api/admin/analytics?days=1&{query}", owner, tenant), ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        }

        JsonElement shopView = default;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            shopView = await Dashboard($"site={shop}");
            if (shopView.GetRawText().Contains("/twice", StringComparison.Ordinal)
                && shopView.GetRawText().Contains("/cart", StringComparison.Ordinal))
            {
                break;
            }
            await Task.Delay(250, ct);
        }
        // Let a second copy of the batch land, if it were going to.
        await Task.Delay(1000, ct);
        shopView = await Dashboard($"site={shop}");

        var paths = shopView.GetProperty("topPaths").EnumerateArray()
            .ToDictionary(p => p.GetProperty("path").GetString()!, p => p.GetProperty("count").GetInt64());
        paths.Should().BeEquivalentTo(new Dictionary<string, long> { ["/cart"] = 1, ["/twice"] = 1 });
        shopView.GetProperty("series").EnumerateArray().Sum(d => d.GetProperty("events").GetInt64())
            .Should().Be(2, "the rollups counted the redelivered batch once");
        shopView.GetProperty("topSources").GetArrayLength().Should().Be(0, "a referrer on the site's own host is not a source");
        shopView.GetProperty("byHostname").EnumerateArray().Single().GetProperty("hostname").GetString()
            .Should().Be("shop.example");

        var blogView = await Dashboard($"site={blog}");
        blogView.GetProperty("topPaths").EnumerateArray().Select(p => p.GetProperty("path").GetString())
            .Should().Equal("/post");

        (await admin.SendAsync(Req(HttpMethod.Get, "/api/admin/analytics?site=nope", owner, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
