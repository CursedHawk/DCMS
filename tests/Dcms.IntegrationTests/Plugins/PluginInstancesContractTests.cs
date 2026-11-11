using System.Net;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.DynamicApps;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// dcms.plugins@1 (ADR 0016): the assistant installs plugins and changes their settings with the
/// console's rules — but configures only instances a person opted in to AI tools, and can never
/// flip that switch itself.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class PluginInstancesContractTests(ContentFlowFixture fixture)
{
    [DockerFact]
    public async Task An_agent_installs_and_configures_plugins_only_where_a_person_let_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);

        var overview = await Json(await Call(app, "List", new { }, ct), ct);
        overview.GetProperty("available").EnumerateArray().Should().Contain(p => p.GetProperty("id").GetString() == "blog");

        var installed = await Json(await Call(app, "Install", new { pluginId = "blog", slug = "news", name = "News" }, ct), ct);
        installed.GetProperty("enabled").GetBoolean().Should().BeTrue();
        installed.GetProperty("aiToolsEnabled").GetBoolean().Should().BeFalse("a new instance's AI tools start off");
        (await Call(app, "Install", new { pluginId = "blog", slug = "news" }, ct)).StatusCode.Should().Be(HttpStatusCode.Conflict, "the console's rules");

        // Its settings are out of reach until a person turns its AI tools on — and the agent cannot.
        (await Call(app, "GetSettings", new { instance = "news" }, ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Call(app, "UpdateSettings", new { instance = "news", aiToolsEnabled = true }, ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "the switch is not part of the contract's input");
        var id = installed.GetProperty("id").GetGuid();
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Put, $"/api/admin/plugins/instances/{id}", app.Owner, app.Tenant,
            body: new { aiToolsEnabled = true }), ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var settings = await Json(await Call(app, "GetSettings", new { instance = "news" }, ct), ct);
        settings.GetProperty("configSchema").GetProperty("properties").TryGetProperty("postsPerPage", out _).Should().BeTrue();
        (await Call(app, "UpdateSettings", new { instance = "news", config = new { postsPerPage = "many" } }, ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "validated against the plugin's schema");
        var updated = await Json(await Call(app, "UpdateSettings", new { instance = "news", config = new { title = "News", postsPerPage = 5 } }, ct), ct);
        updated.GetProperty("config").GetProperty("postsPerPage").GetInt32().Should().Be(5);
        updated.GetProperty("instance").GetProperty("aiToolsEnabled").GetBoolean().Should().BeTrue("a settings change leaves the switch alone");

        var disabled = await Json(await Call(app, "SetEnabled", new { instance = "news", enabled = false }, ct), ct);
        disabled.GetProperty("enabled").GetBoolean().Should().BeFalse();

        // Managing plugins is a permission like any other: without it the contract is not offered.
        var editor = await app.AddMemberAsync(["content:read"], ct);
        var catalog = await Json(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/contracts?plane=ai", editor, app.Tenant), ct), ct);
        catalog.EnumerateArray().Select(c => c.GetProperty("id").GetString()).Should().NotContain("dcms.plugins@1");
    }

    private static Task<HttpResponseMessage> Call(AppHarness app, string op, object input, CancellationToken ct) =>
        app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/contracts/dcms.plugins@1/{op}?plane=ai", app.Owner, app.Tenant, body: input), ct);

    private static async Task<JsonElement> Json(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        response.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
