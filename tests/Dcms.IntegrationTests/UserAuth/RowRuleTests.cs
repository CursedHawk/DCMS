using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.DynamicApps;

namespace Dcms.IntegrationTests.UserAuth;

/// <summary>
/// Row access rules on a Dynamic Apps site API (ADR 0021): which records a signed-in user reaches
/// is data — a permission table naming them, or a group on the record — followed through
/// relationships, so activities inherit their company's access. Every read path (list, query,
/// get, related, expand) and every write path honours it.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class RowRuleTests(ContentFlowFixture fixture)
{
    private const string Model = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "companies", "displayName": "Company", "primaryFieldId": "name",
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true }, { "apiName": "team", "displayName": "Team" } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "company_access", "displayName": "Company access",
              "fields": [ { "apiName": "user_email", "displayName": "User", "type": "email", "required": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "activities", "displayName": "Activity", "primaryFieldId": "subject",
              "public": { "create": true },
              "fields": [ { "apiName": "subject", "displayName": "Subject", "required": true } ] } },
          { "op": "create", "type": "relationship", "value": { "apiName": "company", "sourceTableId": "company_access", "targetTableId": "companies",
              "inverseApiName": "access", "required": true, "onDelete": "cascade" } },
          { "op": "create", "type": "relationship", "value": { "apiName": "company", "sourceTableId": "activities", "targetTableId": "companies",
              "inverseApiName": "activities", "required": true, "onDelete": "cascade" } },
          { "op": "update", "type": "table", "target": "companies", "value": { "public": { "rules": [
              { "path": ["access"], "field": "user_email", "matches": "user.email" },
              { "path": [], "field": "team", "matches": "user.groups", "update": true } ] } } },
          { "op": "update", "type": "table", "target": "activities", "value": { "public": { "create": true, "rules": [
              { "path": ["company", "access"], "field": "user_email", "matches": "user.email", "update": true, "delete": true } ] } } }
        ]
        """;

    [DockerFact]
    public async Task A_permission_table_decides_which_companies_and_their_activities_a_user_reaches()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        var acme = await CreateAsync(app, "companies", new { name = "Acme" }, ct);
        var globex = await CreateAsync(app, "companies", new { name = "Globex" }, ct);
        var grant = await CreateAsync(app, "company_access", new { company = acme, user_email = "Ada@Corp.test" }, ct);
        var acmeCall = await CreateAsync(app, "activities", new { subject = "Acme call", company = acme }, ct);
        var globexCall = await CreateAsync(app, "activities", new { subject = "Globex call", company = globex }, ct);
        var ada = RealmTokens.Mint(tenantId, Guid.NewGuid(), email: "ada@corp.test");
        var bob = RealmTokens.Mint(tenantId, Guid.NewGuid(), email: "bob@corp.test");

        (await SiteAsync(app, HttpMethod.Get, "/api/crm/data/companies", null, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Names(await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/data/companies", ada, ct), ct), "name")
            .Should().Equal(["Acme"], "the email is compared without regard to case");
        Names(await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/data/companies", bob, ct), ct), "name").Should().BeEmpty();
        (await SiteAsync(app, HttpMethod.Get, $"/api/crm/data/companies/{globex}", ada, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SiteAsync(app, HttpMethod.Get, "/api/crm/data/company_access", ada, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the permission table itself stays private");

        // Activities inherit their company's access, through every read path.
        Names(await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/data/activities?expand=company", ada, ct), ct), "subject")
            .Should().Equal(["Acme call"]);
        var query = await JsonAsync(await SiteAsync(app, HttpMethod.Post, "/api/crm/data/activities/query", ada, ct,
            new { filter = new { field = "subject", op = "contains", value = "call" } }), ct);
        query.GetProperty("total").GetInt32().Should().Be(1, "the count is of reachable records too");
        Names(await JsonAsync(await SiteAsync(app, HttpMethod.Get, $"/api/crm/data/companies/{acme}/activities", ada, ct), ct), "subject")
            .Should().Equal(["Acme call"]);
        (await SiteAsync(app, HttpMethod.Get, $"/api/crm/data/activities/{globexCall}", ada, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Writes: only under a company they reach, and only records the rule gives them.
        (await SiteAsync(app, HttpMethod.Post, "/api/crm/data/activities", ada, ct, new { subject = "Sneaky", company = globex }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a lookup from the site points only at a record the caller can read");
        (await SiteAsync(app, HttpMethod.Post, "/api/crm/data/activities", ada, ct, new { subject = "Follow-up", company = acme }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await SiteAsync(app, HttpMethod.Patch, $"/api/crm/data/activities/{globexCall}", ada, ct, new { subject = "Mine now" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SiteAsync(app, HttpMethod.Patch, $"/api/crm/data/activities/{acmeCall}", ada, ct, new { company = globex }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "nor can a record be moved under one they cannot see");
        (await SiteAsync(app, HttpMethod.Patch, $"/api/crm/data/activities/{acmeCall}", ada, ct, new { subject = "Acme call (done)" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await SiteAsync(app, HttpMethod.Patch, $"/api/crm/data/companies/{acme}", ada, ct, new { name = "Acme Inc" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "her company rule grants reading, not changing");
        (await SiteAsync(app, HttpMethod.Delete, $"/api/crm/data/activities/{acmeCall}", bob, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SiteAsync(app, HttpMethod.Delete, $"/api/crm/data/activities/{acmeCall}", ada, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Access is data: removing the row takes it away at once.
        (await app.SendAsync(HttpMethod.Delete, $"/_records/company_access/{grant}", null, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Names(await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/data/activities", ada, ct), ct), "subject").Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_group_on_the_record_lets_its_members_read_and_change_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        var sales = Guid.NewGuid();
        var initech = await CreateAsync(app, "companies", new { name = "Initech", team = sales.ToString().ToUpperInvariant() }, ct);
        await CreateAsync(app, "companies", new { name = "Umbrella", team = Guid.NewGuid().ToString() }, ct);
        var seller = RealmTokens.Mint(tenantId, Guid.NewGuid(), [Guid.NewGuid(), sales], email: "sam@corp.test");
        var outsider = RealmTokens.Mint(tenantId, Guid.NewGuid(), [Guid.NewGuid()], email: "out@corp.test");

        Names(await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/data/companies", seller, ct), ct), "name").Should().Equal(["Initech"]);
        Names(await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/data/companies", outsider, ct), ct), "name").Should().BeEmpty();
        (await SiteAsync(app, HttpMethod.Patch, $"/api/crm/data/companies/{initech}", seller, ct, new { name = "Initech Ltd" }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the group rule grants changing too");
        (await SiteAsync(app, HttpMethod.Patch, $"/api/crm/data/companies/{initech}", outsider, ct, new { name = "Hijacked" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var model = await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/_model", null, ct), ct);
        var companies = model.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("apiName").GetString() == "companies");
        companies.GetProperty("access").GetProperty("rowRules").GetBoolean().Should().BeTrue();
        companies.GetProperty("access").TryGetProperty("rules", out _).Should().BeFalse("what the rules test is not the site's business");
        model.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("apiName").GetString()).Should().NotContain("company_access");
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

    private static async Task<string> CreateAsync(AppHarness app, string table, object values, CancellationToken ct) =>
        (await app.JsonAsync(HttpMethod.Post, $"/_records/{table}", ct, values, HttpStatusCode.Created)).GetProperty("id").GetString()!;

    private static IEnumerable<string?> Names(JsonElement page, string field) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty(field).GetString()).Order();

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

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
