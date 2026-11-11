using System.Net;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.DynamicApps;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// The plugins' console actions as AI-plane operations (epic #624): forms.inbox, visitors.accounts
/// and functions.ops — each in a contract of its own, so the read contracts functions already
/// use did not grow write permissions, and secrets only ever going in.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class PluginAiOpsTests(ContentFlowFixture fixture)
{
    [DockerFact]
    public async Task An_agent_works_the_form_inbox_and_visitor_accounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.InstallAsync("forms", "contact", """{"forms":[{"name":"hello","fields":[{"name":"message","required":true}]}]}""", ct);
        await app.InstallAsync("visitor-auth", "members", "{}", ct);
        await OptInAsync(app, "contact", ct);
        await OptInAsync(app, "members", ct);

        // Forms: the inbox's two actions, beside the read contract that stays read-only.
        (await app.SiteAsync(HttpMethod.Post, "/api/contact/forms/hello", ct, new { message = "hi" })).IsSuccessStatusCode.Should().BeTrue();
        var page = await Json(await Call(app, "forms.submissions", "List", "contact", new { }, ct), ct);
        var submission = page.GetProperty("items")[0].GetProperty("id").GetGuid();
        (await Call(app, "forms.inbox", "MarkHandled", "contact", new { submissionId = submission }, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Json(await Call(app, "forms.submissions", "Get", "contact", new { submissionId = submission }, ct), ct))
            .GetProperty("handled").GetBoolean().Should().BeTrue();
        (await Call(app, "forms.inbox", "Delete", "contact", new { submissionId = submission }, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Call(app, "forms.inbox", "Delete", "contact", new { submissionId = submission }, ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Visitors: found by email, verified, deleted.
        await app.VisitorAsync("pat@visitor.test", ct);
        var found = await Json(await Call(app, "visitors.accounts", "Find", "members", new { search = "pat@" }, ct), ct);
        var visitor = found.GetProperty("items")[0];
        visitor.GetProperty("emailVerified").GetBoolean().Should().BeFalse();
        var visitorId = visitor.GetProperty("id").GetGuid();
        (await Call(app, "visitors.accounts", "MarkEmailVerified", "members", new { visitorId }, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Json(await Call(app, "visitors.accounts", "Find", "members", new { search = "pat@" }, ct), ct))
            .GetProperty("items")[0].GetProperty("emailVerified").GetBoolean().Should().BeTrue();
        (await Call(app, "visitors.accounts", "Delete", "members", new { visitorId }, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Json(await Call(app, "visitors.accounts", "Find", "members", new { search = "pat@" }, ct), ct))
            .GetProperty("total").GetInt64().Should().Be(0);

        // A member who may read content but not write it reads the inbox and cannot change it.
        var reader = await app.AddMemberAsync(["content:read"], ct);
        (await Call(app, "forms.inbox", "MarkHandled", "contact", new { submissionId = Guid.NewGuid() }, ct, reader))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [DockerFact]
    public async Task An_agent_sets_a_functions_secret_it_can_never_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.InstallAsync("functions", "fx", "{}", ct);
        await OptInAsync(app, "fx", ct);

        var set = await Call(app, "functions.ops", "SetSecret", "fx", new { name = "STRIPE_KEY", value = "sk_live_never_echoed" }, ct);
        set.StatusCode.Should().Be(HttpStatusCode.NoContent, await set.Content.ReadAsStringAsync(ct));
        var listed = await (await Call(app, "functions.ops", "ListSecrets", "fx", new { }, ct)).Content.ReadAsStringAsync(ct);
        listed.Should().Contain("STRIPE_KEY").And.NotContain("sk_live_never_echoed");
        (await Call(app, "functions.ops", "ListKeys", "fx", new { }, ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Call(app, "functions.ops", "DeleteSecret", "fx", new { name = "STRIPE_KEY" }, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // No operation makes a credential: a new API key is the console's alone.
        var catalog = await Json(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/contracts?plane=ai", app.Owner, app.Tenant), ct), ct);
        var ops = catalog.EnumerateArray().Single(c => c.GetProperty("id").GetString() == "functions.ops@1")
            .GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("name").GetString()).ToList();
        ops.Should().Contain(["ListRuns", "RevokeKey", "SetSecret"]).And.NotContain(o => o!.StartsWith("Create", StringComparison.Ordinal));
    }

    private static async Task OptInAsync(AppHarness app, string slug, CancellationToken ct)
    {
        var instances = await Json(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/plugins/instances", app.Owner, app.Tenant), ct), ct);
        var id = instances.EnumerateArray().Single(i => i.GetProperty("slug").GetString() == slug).GetProperty("id").GetGuid();
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Put, $"/api/admin/plugins/instances/{id}", app.Owner, app.Tenant,
            body: new { aiToolsEnabled = true }), ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static Task<HttpResponseMessage> Call(AppHarness app, string contract, string op, string instance, object input, CancellationToken ct,
        Guid? @as = null) =>
        app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/contracts/{contract}@1/{op}?instance={instance}&plane=ai", @as ?? app.Owner,
            app.Tenant, body: input), ct);

    private static async Task<JsonElement> Json(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        response.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
