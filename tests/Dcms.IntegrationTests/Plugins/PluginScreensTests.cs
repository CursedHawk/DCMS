using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// Plugins' own admin screens (ADR 0019): the menu shows a plugin's screens while an instance
/// is enabled and drops them when it is switched off; the console learns where each plugin's
/// UI module lives and which screens the caller may open.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class PluginScreensTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task A_plugins_menu_entries_follow_whether_it_is_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fixture.Admin.CreateClient();
        var owner = Guid.NewGuid();
        var tenant = "scr-" + Guid.NewGuid().ToString("N")[..8];
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug = tenant, name = tenant, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        async Task<List<string>> Menu() =>
            (await Json(admin, Req(HttpMethod.Get, "/api/admin/navigation", owner, tenant), ct))
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("to").GetString()!).ToList();

        (await Menu()).Should().NotContain("/app/forms/inbox", "no Forms instance yet");

        var created = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            body: new { pluginId = "forms", slug = "contact", name = "Contact", description = "Contact form", config = "{}" }), ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        var menu = await Menu();
        menu.Should().Contain("/app/forms/inbox");
        menu.Should().NotContain("/forms", "the inbox is the plugin's screen now, not a platform section");

        (await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/plugins/instances/{id}/disable", owner, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Menu()).Should().NotContain("/app/forms/inbox", "a switched-off plugin has no menu entries");

        var ui = await Json(admin, Req(HttpMethod.Get, "/api/admin/plugin-ui", owner, tenant), ct);
        var forms = ui.EnumerateArray().Single(p => p.GetProperty("pluginId").GetString() == "forms");
        forms.GetProperty("module").GetProperty("key").GetString().Should().Be("Dcms.Plugins.Forms");
        var inbox = forms.GetProperty("screens").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "inbox");
        inbox.GetProperty("allowed").GetBoolean().Should().BeTrue("the Owner holds content:read");
        forms.GetProperty("instances")[0].GetProperty("enabled").GetBoolean().Should().BeFalse("disabled instances are still listed");
    }

    private static async Task<JsonElement> Json(HttpClient client, HttpRequestMessage req, CancellationToken ct)
    {
        var res = await client.SendAsync(req, ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string slug, string roles = "", object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (!string.IsNullOrEmpty(roles)) req.Headers.Add("X-Test-Roles", roles);
        if (slug.Length > 0) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = System.Net.Http.Json.JsonContent.Create(body);
        return req;
    }
}
