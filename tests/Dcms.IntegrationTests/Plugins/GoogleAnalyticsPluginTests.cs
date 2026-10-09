using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.Plugins.GoogleAnalytics;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// The Google Analytics plugin: a Measurement ID per site in its instance config, served to the
/// site runtimes by <c>GET /api/ga/config</c> for the site site-host says the request is for.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class GoogleAnalyticsPluginTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task Each_site_gets_its_own_id_and_a_disabled_plugin_serves_none()
    {
        var ct = TestContext.Current.CancellationToken;
        Guid shop = Guid.NewGuid(), blog = Guid.NewGuid();
        var config = JsonSerializer.Serialize(new { sites = new Dictionary<string, string> { [shop.ToString()] = "G-SHOP1234" } });

        var (owner, tenant) = await TenantAsync(ct);
        var admin = fixture.Admin.CreateClient();
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            new { pluginId = GoogleAnalyticsPlugin.PluginId, slug = "google", name = "GA", config = """{"sites":{"x":"UA-1"}}""" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "the schema only takes G- Measurement IDs");
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            new { pluginId = GoogleAnalyticsPlugin.PluginId, slug = "google", name = "GA", config }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await ConfigAsync(tenant, shop, ct)).GetProperty("measurementId").GetString().Should().Be("G-SHOP1234");
        (await ConfigAsync(tenant, blog, ct)).TryGetProperty("measurementId", out _).Should().BeFalse();
        (await ConfigAsync(tenant, null, ct)).TryGetProperty("measurementId", out _).Should().BeFalse(
            "a request site-host did not resolve (an external site) names no site");

        // A second workspace whose instance is switched off before anything is asked.
        var (owner2, tenant2) = await TenantAsync(ct);
        var created = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner2, tenant2,
            new { pluginId = GoogleAnalyticsPlugin.PluginId, slug = "google", name = "GA", config }), ct);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        (await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/plugins/instances/{id}/disable", owner2, tenant2), ct))
            .IsSuccessStatusCode.Should().BeTrue();
        (await ConfigAsync(tenant2, shop, ct)).TryGetProperty("measurementId", out _).Should().BeFalse();
    }

    [Fact]
    public void Only_well_formed_ids_keyed_by_a_site_id_are_served()
    {
        var site = Guid.NewGuid();
        GoogleAnalyticsPlugin.MeasurementIds($$$"""{"sites":{"{{{site}}}":"G-OK12345","not-a-guid":"G-OK12345","{{{Guid.NewGuid()}}}":"G-x\"><script>"}}""")
            .Should().BeEquivalentTo(new Dictionary<string, string> { [site.ToString()] = "G-OK12345" });
        GoogleAnalyticsPlugin.MeasurementIds("not json").Should().BeEmpty();
    }

    private async Task<JsonElement> ConfigAsync(string tenant, Guid? site, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/ga/config");
        req.Headers.Add("X-Dcms-Tenant", tenant);
        if (site is { } s) req.Headers.Add("X-Dcms-Site", s.ToString());
        var res = await fixture.Content.CreateClient().SendAsync(req, ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromMinutes(5));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task<(Guid Owner, string Tenant)> TenantAsync(CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var tenant = "ga-" + Guid.NewGuid().ToString("N")[..8];
        (await fixture.Admin.CreateClient().SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "",
            new { slug = tenant, name = tenant, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }, "SuperAdmin"), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        return (owner, tenant);
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string slug, object? body = null, string roles = "")
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
