using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// Plan M4b end to end: search served by the Search plugin over dcms.search@1, a contract
/// binding in a config schema that the server still validates, and the catalog stating what
/// each plugin provides and consumes.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class PluginContractWiringTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task Search_is_served_by_an_enabled_search_instance_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, owner) = await TenantAsync(ct);
        var content = fixture.Content.CreateClient();

        var before = await content.SendAsync(Get(tenant, "/api/find/search?q=anything"), ct);
        before.StatusCode.Should().Be(HttpStatusCode.NotFound, "no Search instance yet");

        await CreateInstanceAsync(tenant, owner, "search", "find", "{}", ct);

        var after = await content.SendAsync(Get(tenant, "/api/find/search?q=anything"), ct);
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await after.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("total").GetInt64().Should().Be(0);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [DockerFact]
    public async Task Events_binds_its_roster_by_slug_and_the_schema_still_validates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, owner) = await TenantAsync(ct);
        await CreateInstanceAsync(tenant, owner, "roster", "crew", "{}", ct);

        var res = await CreateInstanceAsync(tenant, owner, "events", "gigs", """{"rosterSlug":"crew"}""", ct);
        res.Should().Be(HttpStatusCode.Created);

        var bad = await CreateInstanceAsync(tenant, owner, "events", "gigs2", """{"rosterSlug":42}""", ct);
        bad.Should().Be(HttpStatusCode.BadRequest, "the binding marker does not switch off schema validation");
    }

    [DockerFact]
    public async Task Catalog_states_what_each_plugin_provides_and_consumes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, owner) = await TenantAsync(ct);
        var admin = fixture.Admin.CreateClient();

        var res = await admin.SendAsync(AdminReq(HttpMethod.Get, "/api/admin/plugins/catalog", owner, tenant), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var catalog = (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!);

        catalog["visitor-auth"].GetProperty("provides").EnumerateArray().Select(p => p.GetString())
            .Should().BeEquivalentTo(["visitors.identity@1", "visitors.profiles@1", "automation.actions@1"]);
        catalog["forms"].GetProperty("consumes").EnumerateArray()
            .Should().Contain(c => c.GetProperty("contractId").GetString() == "visitors.identity@1"
                                   && c.GetProperty("optional").GetBoolean());
    }

    [DockerFact]
    public async Task The_marketplace_serves_a_plugins_developer_reference()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, owner) = await TenantAsync(ct);
        var admin = fixture.Admin.CreateClient();

        var res = await admin.SendAsync(AdminReq(HttpMethod.Get, "/api/admin/marketplace/forms/reference", owner, tenant), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("provides")[0].GetProperty("hooks")[0].GetProperty("name").GetString().Should().Be("forms.submitting");
        body.GetProperty("cSharp").GetString().Should().Contain("Dcms.Plugins.Forms.Api");

        (await admin.SendAsync(AdminReq(HttpMethod.Get, "/api/admin/marketplace/nope/reference", owner, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- helpers ----

    private async Task<(string Slug, Guid Owner)> TenantAsync(CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var slug = "wire-" + Guid.NewGuid().ToString("N")[..8];
        var res = await fixture.Admin.CreateClient().SendAsync(AdminReq(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (slug, owner);
    }

    private async Task<HttpStatusCode> CreateInstanceAsync(string tenant, Guid owner, string pluginId, string slug, string config, CancellationToken ct)
    {
        var res = await fixture.Admin.CreateClient().SendAsync(AdminReq(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            body: new { pluginId, slug, name = slug, config }), ct);
        if (res.StatusCode == HttpStatusCode.InternalServerError)
        {
            Assert.Fail(await res.Content.ReadAsStringAsync(ct));
        }
        return res.StatusCode;
    }

    private static HttpRequestMessage Get(string tenant, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Dcms-Tenant", tenant);
        return req;
    }

    private static HttpRequestMessage AdminReq(HttpMethod method, string url, Guid sub, string slug, string roles = "", object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (!string.IsNullOrEmpty(roles)) req.Headers.Add("X-Test-Roles", roles);
        if (slug.Length > 0) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }
}
