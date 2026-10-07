using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.DynamicApps;

namespace Dcms.IntegrationTests.UserAuth;

/// <summary>
/// Enterprise users on a Dynamic Apps site API (ADR 0022, UA5): a role's
/// <c>dynamic-apps:{slug}:table:{t}:{action}</c> lifts the table's public access for its holders —
/// every record, not only their own — and <c>…:flow:{f}:run</c> lets them start a manual flow.
/// Without one, the public access is all there is.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class MemberAccessTests(ContentFlowFixture fixture)
{
    private const string Model = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal", "pluralName": "Deals",
              "fields": [ { "apiName": "title", "displayName": "Title", "required": true },
                          { "apiName": "margin", "displayName": "Margin", "type": "decimal", "hiddenFromPublic": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "notes", "displayName": "Note",
              "public": { "read": "own", "create": true, "updateOwn": true },
              "fields": [ { "apiName": "body", "displayName": "Body", "required": true } ] } },
          { "op": "create", "type": "flow", "value": { "apiName": "open_deal", "displayName": "Open a deal", "trigger": { "event": "manual" },
              "steps": [ { "id": "make", "action": "records.create@1", "input": { "table": "deals", "values": { "title": "{{ input.title }}" } } } ] } }
        ]
        """;

    [DockerFact]
    public async Task A_role_opens_a_private_table_to_its_holders_and_nobody_else()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        await app.JsonAsync(HttpMethod.Post, "/_records/deals", ct, new { title = "Big one", margin = 0.4 }, HttpStatusCode.Created);
        var sales = Guid.NewGuid();
        await GrantAsync(app, sales, ["dynamic-apps:crm:table:deals:read", "dynamic-apps:crm:table:deals:create"], ct);
        var seller = RealmTokens.Mint(tenantId, Guid.NewGuid(), [sales]);
        var stranger = RealmTokens.Mint(tenantId, Guid.NewGuid());

        (await SiteAsync(app, HttpMethod.Get, "/api/crm/data/deals", null, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound, "a private table, to the public");
        (await SiteAsync(app, HttpMethod.Get, "/api/crm/data/deals", stranger, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound, "and to a user without the role");

        var deals = await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/data/deals", seller, ct), HttpStatusCode.OK, ct);
        var deal = deals.GetProperty("items").EnumerateArray().Single();
        deal.GetProperty("title").GetString().Should().Be("Big one", "made in the admin, not by them: members read every record");
        deal.TryGetProperty("margin", out _).Should().BeFalse("a hidden field stays hidden: members are still the site");

        (await SiteAsync(app, HttpMethod.Post, "/api/crm/data/deals", seller, ct, new { title = "Theirs" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await SiteAsync(app, HttpMethod.Delete, $"/api/crm/data/deals/{deal.GetProperty("id").GetString()}", seller, ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "read and create, not delete");
        (await SiteAsync(app, HttpMethod.Post, "/api/crm/data/deals", stranger, ct, new { title = "No" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Without_a_role_an_own_table_stays_own_and_with_one_it_is_everyones()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        var ada = RealmTokens.Mint(tenantId, Guid.NewGuid(), email: "ada@corp.test");
        var bob = RealmTokens.Mint(tenantId, Guid.NewGuid(), email: "bob@corp.test");
        (await SiteAsync(app, HttpMethod.Post, "/api/crm/data/notes", ada, ct, new { body = "Ada's" })).StatusCode.Should().Be(HttpStatusCode.Created);

        (await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/data/notes", bob, ct), HttpStatusCode.OK, ct))
            .GetProperty("total").GetInt32().Should().Be(0, "an enterprise user is a visitor to own-records tables: only their own");

        var support = Guid.NewGuid();
        await GrantAsync(app, support, ["dynamic-apps:crm:table:notes:read"], ct);
        var agent = RealmTokens.Mint(tenantId, Guid.NewGuid(), [support]);
        (await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/data/notes", agent, ct), HttpStatusCode.OK, ct))
            .GetProperty("total").GetInt32().Should().Be(1, "reading every note is what the role is for");
    }

    [DockerFact]
    public async Task A_manual_flow_starts_from_the_site_only_for_holders_of_its_run_permission()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        var managers = Guid.NewGuid();
        await GrantAsync(app, managers, ["dynamic-apps:crm:flow:open_deal:run"], ct);

        (await SiteAsync(app, HttpMethod.Post, "/api/crm/flows/open_deal/run", null, ct, new { input = new { title = "x" } }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SiteAsync(app, HttpMethod.Post, "/api/crm/flows/open_deal/run", RealmTokens.Mint(tenantId, Guid.NewGuid()), ct, new { input = new { title = "x" } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var manager = RealmTokens.Mint(tenantId, Guid.NewGuid(), [managers]);
        var started = await JsonAsync(await SiteAsync(app, HttpMethod.Post, "/api/crm/flows/open_deal/run", manager, ct, new { input = new { title = "From the site" } }),
            HttpStatusCode.Accepted, ct);
        var runId = started.GetProperty("runId").GetGuid();
        (await app.JsonAsync(HttpMethod.Get, $"/_automation/runs/{runId}", ct)).GetProperty("run").GetProperty("flow").GetString()
            .Should().Be("open_deal");
    }

    [DockerFact]
    public async Task The_app_offers_its_tables_and_manual_flows_to_the_role_editor()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, _) = await SetUpAsync(ct);
        var catalogs = await JsonAsync(await app.Admin.SendAsync(
            AppHarness.Req(HttpMethod.Get, "/api/admin/plugins/users/resources", app.Owner, app.Tenant), ct), HttpStatusCode.OK, ct);
        var crm = catalogs.EnumerateArray().Single(c => c.GetProperty("instance").GetString() == "crm");
        crm.GetProperty("plugin").GetString().Should().Be("dynamic-apps");
        crm.GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("resource").GetString())
            .Should().Equal("table:deals", "table:notes", "flow:open_deal");
    }

    private async Task<(AppHarness App, Guid TenantId)> SetUpAsync(CancellationToken ct)
    {
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.InstallAsync("user-auth", "users", "{}", ct);
        await app.PublishAsync(Model, ct);
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @p""";
        cmd.Parameters.AddWithValue("p", app.Tenant);
        return (app, (Guid)(await cmd.ExecuteScalarAsync(ct))!);
    }

    private static async Task GrantAsync(AppHarness app, Guid group, string[] permissions, CancellationToken ct)
    {
        var role = await JsonAsync(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/plugins/users/roles", app.Owner, app.Tenant,
            body: new { key = $"r_{Guid.NewGuid():N}"[..20], name = "Role", permissions }), ct), HttpStatusCode.Created, ct);
        await JsonAsync(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/plugins/users/roles/{role.GetProperty("id").GetGuid()}/grants",
            app.Owner, app.Tenant, body: new { subjectType = "group", subjectId = group }), ct), HttpStatusCode.Created, ct);
    }

    private Task<HttpResponseMessage> SiteAsync(AppHarness app, HttpMethod method, string url, string? token, CancellationToken ct, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Dcms-Tenant", app.Tenant);
        if (token is not null)
        {
            req.Headers.Add("X-Dcms-Realm-Token", token);
        }
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        return fixture.Content.CreateClient().SendAsync(req, ct);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.Should().Be(expected, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
