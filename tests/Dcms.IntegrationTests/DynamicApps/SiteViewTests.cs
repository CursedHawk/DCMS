using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.UserAuth;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>
/// Saved views of an app's tables on the tenant's sites (#297): the app's own views for anyone who
/// may read the table; a signed-in user's own, shared read-only with everyone, groups or people;
/// only fields the site can see; never a table the user could not open.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class SiteViewTests(ContentFlowFixture fixture)
{
    private const string Model = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "deals", "displayName": "Deal", "primaryFieldId": "name",
              "public": { "read": "all" },
              "fields": [ { "apiName": "name", "displayName": "Name", "required": true },
                          { "apiName": "amount", "displayName": "Amount", "type": "decimal" },
                          { "apiName": "margin", "displayName": "Margin", "type": "decimal", "hiddenFromPublic": true } ] } },
          { "op": "create", "type": "table", "value": { "apiName": "notes", "displayName": "Note",
              "fields": [ { "apiName": "body", "displayName": "Body" } ] } },
          { "op": "create", "type": "view", "value": { "apiName": "big", "displayName": "Big deals", "tableId": "deals",
              "columns": ["name", "amount", "margin"], "sort": [ { "fieldId": "amount", "descending": true } ], "isDefault": true } }
        ]
        """;

    private static readonly object Definition = new
    {
        columns = new[] { "name", "amount" },
        sort = new[] { new { field = "amount", direction = "desc" } },
        filter = new { or = new object[] { new { field = "amount", op = "gt", value = 100 }, new { field = "name", op = "contains", value = "acme" } } },
    };

    [DockerFact]
    public async Task A_user_saves_a_view_and_shares_it_with_everyone_groups_or_people()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        var sales = fixture.Realms.AddGroup(tenantId, "Sales");
        var adaId = fixture.Realms.AddUser(tenantId, "ada@corp.test");
        var bobId = fixture.Realms.AddUser(tenantId, "bob@corp.test", "active", sales);
        var carolId = fixture.Realms.AddUser(tenantId, "carol@corp.test");
        var gone = fixture.Realms.AddUser(tenantId, "gone@corp.test", "disabled");
        var ada = RealmTokens.Mint(tenantId, adaId, email: "ada@corp.test");
        var bob = RealmTokens.Mint(tenantId, bobId, [sales], email: "bob@corp.test");
        var carol = RealmTokens.Mint(tenantId, carolId, email: "carol@corp.test");

        // Anyone gets the app's own views, as far as the site may see them; nobody can save one.
        var open = await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/views/deals", null, ct), ct);
        open.GetProperty("signedIn").GetBoolean().Should().BeFalse();
        var big = open.GetProperty("views")[0];
        big.GetProperty("id").GetString().Should().StartWith("system:");
        big.GetProperty("definition").GetProperty("columns").EnumerateArray().Select(c => c.GetString()).Should().Equal(["name", "amount"],
            "a field hidden from the site is not a column there");
        open.GetProperty("defaultView").GetString().Should().Be(big.GetProperty("id").GetString());
        (await SiteAsync(app, HttpMethod.Post, "/api/crm/views/deals", null, ct, new { name = "Mine", definition = Definition }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Ada saves one: hers alone until she shares it.
        var saved = await JsonAsync(await SiteAsync(app, HttpMethod.Post, "/api/crm/views/deals", ada, ct, new { name = "Hot", definition = Definition }),
            ct, HttpStatusCode.Created);
        var id = saved.GetProperty("id").GetString();
        saved.GetProperty("mine").GetBoolean().Should().BeTrue();
        Names(await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/views/deals", bob, ct), ct)).Should().Equal("Big deals");

        // Whom she can share with: the site's active people and its groups.
        var people = await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/views/_people?search=corp", ada, ct), ct);
        people.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("email").GetString()).Should()
            .Equal("ada@corp.test", "bob@corp.test", "carol@corp.test");
        (await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/views/_groups", ada, ct), ct)).GetProperty("groups")
            .EnumerateArray().Select(g => g.GetProperty("name").GetString()).Should().Equal("Sales");
        (await SiteAsync(app, HttpMethod.Get, "/api/crm/views/_people", null, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // With a group: Bob sees it, read-only, and whose it is — not whom else it went to.
        await JsonAsync(await SiteAsync(app, HttpMethod.Put, $"/api/crm/views/deals/{id}/sharing", ada, ct, new { everyone = false, groups = new[] { sales } }), ct);
        var bobs = (await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/views/deals", bob, ct), ct)).GetProperty("views").EnumerateArray()
            .Single(v => v.GetProperty("id").GetString() == id);
        bobs.GetProperty("mine").GetBoolean().Should().BeFalse();
        bobs.GetProperty("owner").GetString().Should().Be("Pat", "the owner's name as their token gave it");
        bobs.GetProperty("sharing").ValueKind.Should().Be(JsonValueKind.Null);
        Names(await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/views/deals", carol, ct), ct)).Should().Equal("Big deals");
        (await SiteAsync(app, HttpMethod.Put, $"/api/crm/views/deals/{id}", bob, ct, new { name = "Mine now", definition = Definition }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "only the owner changes a view");
        (await SiteAsync(app, HttpMethod.Delete, $"/api/crm/views/deals/{id}", bob, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // With a person, then with everyone.
        await JsonAsync(await SiteAsync(app, HttpMethod.Put, $"/api/crm/views/deals/{id}/sharing", ada, ct, new { everyone = false, users = new[] { carolId } }), ct);
        Names(await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/views/deals", carol, ct), ct)).Should().Equal("Big deals", "Hot");
        Names(await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/views/deals", bob, ct), ct)).Should().Equal("Big deals");
        await JsonAsync(await SiteAsync(app, HttpMethod.Put, $"/api/crm/views/deals/{id}/sharing", ada, ct, new { everyone = true }), ct);
        Names(await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/views/deals", bob, ct), ct)).Should().Equal("Big deals", "Hot");

        // Nobody outside the site's directory, and no group of another site's.
        (await SiteAsync(app, HttpMethod.Put, $"/api/crm/views/deals/{id}/sharing", ada, ct, new { everyone = false, users = new[] { gone } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SiteAsync(app, HttpMethod.Put, $"/api/crm/views/deals/{id}/sharing", ada, ct, new { everyone = false, groups = new[] { Guid.NewGuid() } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Bob opens the table with it; when Ada deletes it, he opens it with the app's default again.
        (await SiteAsync(app, HttpMethod.Put, "/api/crm/views/deals/default", bob, ct, new { viewKey = id })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/views/deals", bob, ct), ct)).GetProperty("defaultView").GetString().Should().Be(id);
        (await SiteAsync(app, HttpMethod.Delete, $"/api/crm/views/deals/{id}", ada, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/views/deals", bob, ct), ct)).GetProperty("defaultView").GetString()
            .Should().Be(big.GetProperty("id").GetString());
    }

    [DockerFact]
    public async Task A_view_names_only_what_the_site_sees_of_a_table_it_may_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        var ada = RealmTokens.Mint(tenantId, fixture.Realms.AddUser(tenantId, "ada@corp.test"));

        (await SiteAsync(app, HttpMethod.Post, "/api/crm/views/deals", ada, ct,
                new { name = "Margins", definition = new { columns = new[] { "name", "margin" } } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "margin is hidden from the site");
        (await SiteAsync(app, HttpMethod.Post, "/api/crm/views/deals", ada, ct,
                new { name = "By margin", definition = new { filter = new { not = new { field = "margin", op = "gt", value = 1 } } } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "nor can a filter test it");
        (await SiteAsync(app, HttpMethod.Get, "/api/crm/views/notes", ada, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a table the site may not read has no views either");
        (await SiteAsync(app, HttpMethod.Post, "/api/crm/views/deals", ada, ct, new { name = "", definition = Definition }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SiteAsync(app, HttpMethod.Post, "/api/crm/views/deals", ada, ct, new { name = "Recent", definition = new { sort = new[] { new { field = "updated_at", direction = "desc" } } } }))
            .StatusCode.Should().Be(HttpStatusCode.Created, "the record's own dates are always there");

        // A token for another tenant is nobody here.
        var stranger = RealmTokens.Mint(Guid.NewGuid(), Guid.NewGuid());
        (await SiteAsync(app, HttpMethod.Post, "/api/crm/views/deals", stranger, ct, new { name = "X", definition = Definition }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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

    private static IEnumerable<string?> Names(JsonElement list) =>
        list.GetProperty("views").EnumerateArray().Select(v => v.GetProperty("name").GetString());

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

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken ct, HttpStatusCode status = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.Should().Be(status, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
