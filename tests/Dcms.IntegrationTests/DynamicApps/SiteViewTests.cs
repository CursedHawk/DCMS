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

    private const string Tasks = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "tasks", "displayName": "Task", "primaryFieldId": "title",
              "public": { "read": "all" },
              "fields": [ { "apiName": "title", "displayName": "Title", "required": true },
                          { "apiName": "owner_email", "displayName": "Owner", "type": "email" },
                          { "apiName": "team", "displayName": "Team" } ] } }
        ]
        """;

    /// <summary>
    /// "The current user" in a filter (#302): one shared view, and each person who opens it sees
    /// their own rows — the server puts whoever asks in the token's place.
    /// </summary>
    [DockerFact]
    public async Task A_shared_view_about_the_current_user_shows_each_viewer_their_own_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        await app.PublishAsync(Tasks, ct);
        var sales = fixture.Realms.AddGroup(tenantId, "Sales");
        var ada = RealmTokens.Mint(tenantId, fixture.Realms.AddUser(tenantId, "ada@corp.test"), email: "ada@corp.test");
        var bob = RealmTokens.Mint(tenantId, fixture.Realms.AddUser(tenantId, "bob@corp.test", "active", sales), [sales], email: "bob@corp.test");
        foreach (var (title, owner, team) in new[] { ("Call Acme", "ada@corp.test", ""), ("Send quote", "bob@corp.test", sales.ToString()), ("Plan", "eve@corp.test", sales.ToString()) })
        {
            await app.JsonAsync(HttpMethod.Post, "/_records/tasks", ct, new { title, owner_email = owner, team }, HttpStatusCode.Created);
        }

        var mine = new { field = "owner_email", op = "eq", value = new Dictionary<string, string> { ["$user"] = "email" } };
        var saved = await JsonAsync(await SiteAsync(app, HttpMethod.Post, "/api/crm/views/tasks", ada, ct,
            new { name = "My tasks", definition = new { columns = new[] { "title" }, filter = mine } }), ct, HttpStatusCode.Created);
        await JsonAsync(await SiteAsync(app, HttpMethod.Put, $"/api/crm/views/tasks/{saved.GetProperty("id").GetString()}/sharing", ada, ct, new { everyone = true }), ct);

        // Bob opens Ada's view and runs its filter as the table runs any view's: his rows.
        var shared = (await JsonAsync(await SiteAsync(app, HttpMethod.Get, "/api/crm/views/tasks", bob, ct), ct)).GetProperty("views")
            .EnumerateArray().Single(v => v.GetProperty("name").GetString() == "My tasks").GetProperty("definition").GetProperty("filter");
        shared.GetProperty("value").GetProperty("$user").GetString().Should().Be("email", "the view keeps the token, not Ada");
        Titles(await Query(app, ada, shared, ct)).Should().Equal("Call Acme");
        Titles(await Query(app, bob, shared, ct)).Should().Equal("Send quote");
        Titles(await Query(app, null, shared, ct)).Should().BeEmpty("nobody signed in is nobody's rows");
        Titles(await Query(app, bob, new { field = "owner_email", op = "ne", value = new Dictionary<string, string> { ["$user"] = "email" } }, ct))
            .Should().Equal("Call Acme", "Plan");
        Titles(await Query(app, null, new { field = "owner_email", op = "ne", value = new Dictionary<string, string> { ["$user"] = "email" } }, ct))
            .Should().HaveCount(3);

        // Groups: the rows of any of the viewer's teams; none for someone in no group.
        var myTeams = new { field = "team", op = "in", value = new Dictionary<string, string> { ["$user"] = "groups" } };
        Titles(await Query(app, bob, myTeams, ct)).Should().Equal("Plan", "Send quote");
        Titles(await Query(app, ada, myTeams, ct)).Should().BeEmpty();

        // A token that does not fit is refused when saved, not stored.
        (await SiteAsync(app, HttpMethod.Post, "/api/crm/views/tasks", ada, ct, new
        {
            name = "Bad",
            definition = new { filter = new { field = "team", op = "eq", value = new Dictionary<string, string> { ["$user"] = "groups" } } },
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>In the console the current user is the member: "created by me" is their own records.</summary>
    [DockerFact]
    public async Task In_the_console_the_current_user_is_the_member()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, _) = await SetUpAsync(ct);
        await app.PublishAsync(Tasks, ct);
        var writer = await app.AddMemberAsync(["plugin:dynamic-apps:data-read", "plugin:dynamic-apps:data-write"], ct);
        await app.JsonAsync(HttpMethod.Post, "/_records/tasks", ct, new { title = "Owner's" }, HttpStatusCode.Created);
        await app.JsonAsync(HttpMethod.Post, "/_records/tasks", ct, new { title = "Writer's" }, HttpStatusCode.Created, writer);

        var mine = new { filter = new { field = "created_by", op = "eq", value = new Dictionary<string, string> { ["$user"] = "id" } } };
        Titles(await app.JsonAsync(HttpMethod.Post, "/_records/tasks/query", ct, mine)).Should().Equal("Owner's");
        Titles(await app.JsonAsync(HttpMethod.Post, "/_records/tasks/query", ct, mine, @as: writer)).Should().Equal("Writer's");
    }

    private async Task<JsonElement> Query(AppHarness app, string? token, object filter, CancellationToken ct) =>
        await JsonAsync(await SiteAsync(app, HttpMethod.Post, "/api/crm/data/tasks/query", token, ct, new { filter, sort = new[] { new { field = "title" } } }), ct);

    private static IEnumerable<string?> Titles(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).Order();

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
