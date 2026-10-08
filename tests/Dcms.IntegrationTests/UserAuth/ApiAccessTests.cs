using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.DynamicApps;

namespace Dcms.IntegrationTests.UserAuth;

/// <summary>
/// API access (ADR 0022): a plugin instance the tenant restricted answers its <c>/api/{slug}</c>
/// only to its site users — every signed-in one, or holders of <c>{plugin}:{slug}:api:read</c> /
/// <c>…:write</c> — and search and tags stop showing what that API refuses. This is what protects
/// a single-page app's data, whose route changes never reach the edge's path rules.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class ApiAccessTests(ContentFlowFixture fixture)
{
    private const string Model = """
        [
          { "op": "create", "type": "table", "value": { "apiName": "notes", "displayName": "Note",
              "public": { "read": "all", "create": true },
              "fields": [ { "apiName": "body", "displayName": "Body", "required": true } ] } }
        ]
        """;

    [DockerFact]
    public async Task A_restricted_instance_answers_its_permission_holders_and_search_and_tags_follow()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        await app.InstallAsync("search", "find", "{}", ct);
        await app.InstallAsync("blog", "news", "{}", ct);
        var word = "okapi" + Guid.NewGuid().ToString("N")[..6];
        await PublishPostAsync(app, word, ct);
        await PollAsync(async () => await SearchTotalAsync(app, word, null, ct) == 1, ct);

        var news = await InstanceAsync(app, "news", ct);
        news.GetProperty("access").GetString().Should().Be("public", "no rule is public");
        news.GetProperty("readPermission").GetString().Should().Be("blog:news:api:read");
        await SetAccessAsync(app, news.GetProperty("instanceId").GetGuid(), "permission", ct);

        var readers = Guid.NewGuid();
        await GrantAsync(app, readers, ["blog:news:api:read"], ct);
        var reader = RealmTokens.Mint(tenantId, Guid.NewGuid(), [readers]);
        var stranger = RealmTokens.Mint(tenantId, Guid.NewGuid());

        // content-api holds the rules for a few seconds; the anonymous answer turns once it reads them.
        await PollAsync(async () => (await SiteAsync(app, HttpMethod.Get, "/api/news/post/secret", null, ct)).StatusCode == HttpStatusCode.Unauthorized, ct);
        var anonymous = await SiteAsync(app, HttpMethod.Get, "/api/news/post/secret", null, ct);
        anonymous.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await anonymous.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("signInUrl").GetString().Should().Be("/.edge/site/signin");
        (await SiteAsync(app, HttpMethod.Get, "/api/news/post", null, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the list as well as the item");
        foreach (var spelling in new[] { "/API/news/post/secret", "/api/news/post/secret/", "/Api/news/post", "/api/news/_config" })
        {
            (await SiteAsync(app, HttpMethod.Get, spelling, null, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{spelling} reaches the same instance");
        }
        (await SiteAsync(app, HttpMethod.Get, "/api/news/post/secret", stranger, ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var served = await SiteAsync(app, HttpMethod.Get, "/api/news/post/secret", reader, ct);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        (served.Headers.CacheControl is { Private: true, NoStore: true }).Should().BeTrue("an answer for one user is no one else's");

        (await SearchTotalAsync(app, word, null, ct)).Should().Be(0, "search must not show what the API refuses");
        (await SearchTotalAsync(app, word, stranger, ct)).Should().Be(0);
        (await SearchTotalAsync(app, word, reader, ct)).Should().Be(1);
        (await TagsAsync(app, null, ct)).Should().NotContain(word);
        (await TagsAsync(app, reader, ct)).Should().Contain(word);

        var catalogs = await JsonAsync(await AdminAsync(app, HttpMethod.Get, "/resources", ct), HttpStatusCode.OK, ct);
        catalogs.EnumerateArray().Single(c => c.GetProperty("instance").GetString() == "news")
            .GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("resource").GetString())
            .Should().Equal(["api"], "the role editor offers what the rule asks for");
    }

    [DockerFact]
    public async Task Reads_need_read_writes_need_write_and_signed_in_takes_anyone_signed_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        await app.PublishAsync(Model, ct);
        var crm = (await InstanceAsync(app, "crm", ct)).GetProperty("instanceId").GetGuid();
        await SetAccessAsync(app, crm, "permission", ct);
        var readers = Guid.NewGuid();
        var writers = Guid.NewGuid();
        await GrantAsync(app, readers, ["dynamic-apps:crm:api:read"], ct);
        await GrantAsync(app, writers, ["dynamic-apps:crm:api:read", "dynamic-apps:crm:api:write"], ct);
        var reader = RealmTokens.Mint(tenantId, Guid.NewGuid(), [readers]);
        var writer = RealmTokens.Mint(tenantId, Guid.NewGuid(), [writers]);

        await PollAsync(async () => (await SiteAsync(app, HttpMethod.Get, "/api/crm/data/notes", null, ct)).StatusCode == HttpStatusCode.Unauthorized, ct);
        (await SiteAsync(app, HttpMethod.Get, "/api/crm/data/notes", reader, ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SiteAsync(app, HttpMethod.Post, "/api/crm/data/notes", reader, ct, new { body = "no" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SiteAsync(app, HttpMethod.Post, "/api/crm/data/notes", writer, ct, new { body = "yes" })).StatusCode.Should().Be(HttpStatusCode.Created);

        await SetAccessAsync(app, crm, "signedIn", ct);
        var anyone = RealmTokens.Mint(tenantId, Guid.NewGuid());
        await PollAsync(async () => (await SiteAsync(app, HttpMethod.Post, "/api/crm/data/notes", anyone, ct, new { body = "in" })).StatusCode == HttpStatusCode.Created, ct);
        (await SiteAsync(app, HttpMethod.Get, "/api/crm/data/notes", null, ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await SetAccessAsync(app, crm, "public", ct);
        await PollAsync(async () => (await SiteAsync(app, HttpMethod.Get, "/api/crm/data/notes", null, ct)).StatusCode == HttpStatusCode.OK, ct);
    }

    [DockerFact]
    public async Task The_plugins_own_instance_and_unknown_levels_are_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, _) = await SetUpAsync(ct);
        var list = await JsonAsync(await AdminAsync(app, HttpMethod.Get, "/api-access", ct), HttpStatusCode.OK, ct);
        list.EnumerateArray().Select(i => i.GetProperty("slug").GetString()).Should().Equal(["crm"], "user-auth's own API is not gated by itself");

        var crm = list[0].GetProperty("instanceId").GetGuid();
        (await AdminAsync(app, HttpMethod.Put, $"/api-access/{crm}", ct, new { access = "groups" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AdminAsync(app, HttpMethod.Put, $"/api-access/{Guid.NewGuid()}", ct, new { access = "signedIn" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<(AppHarness App, Guid TenantId)> SetUpAsync(CancellationToken ct)
    {
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.InstallAsync("user-auth", "users", "{}", ct);
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @p""";
        cmd.Parameters.AddWithValue("p", app.Tenant);
        return (app, (Guid)(await cmd.ExecuteScalarAsync(ct))!);
    }

    private static async Task PublishPostAsync(AppHarness app, string word, CancellationToken ct)
    {
        var news = (await InstanceAsync(app, "news", ct)).GetProperty("instanceId").GetGuid();
        var created = await JsonAsync(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/content", app.Owner, app.Tenant,
            body: new { pluginInstanceId = news, contentType = "post", slug = "secret", data = new { title = word, body = word, tags = new[] { word } } }), ct),
            HttpStatusCode.Created, ct);
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/content/{created.GetProperty("id").GetGuid()}/publish", app.Owner, app.Tenant), ct))
            .EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> InstanceAsync(AppHarness app, string slug, CancellationToken ct) =>
        (await JsonAsync(await AdminAsync(app, HttpMethod.Get, "/api-access", ct), HttpStatusCode.OK, ct))
            .EnumerateArray().Single(i => i.GetProperty("slug").GetString() == slug);

    private static async Task SetAccessAsync(AppHarness app, Guid instanceId, string access, CancellationToken ct) =>
        (await AdminAsync(app, HttpMethod.Put, $"/api-access/{instanceId}", ct, new { access })).StatusCode.Should().Be(HttpStatusCode.NoContent);

    private static async Task GrantAsync(AppHarness app, Guid group, string[] permissions, CancellationToken ct)
    {
        var role = await JsonAsync(await AdminAsync(app, HttpMethod.Post, "/roles", ct,
            new { key = $"r_{Guid.NewGuid():N}"[..20], name = "Role", permissions }), HttpStatusCode.Created, ct);
        await JsonAsync(await AdminAsync(app, HttpMethod.Post, $"/roles/{role.GetProperty("id").GetGuid()}/grants", ct,
            new { subjectType = "group", subjectId = group }), HttpStatusCode.Created, ct);
    }

    private async Task<int> SearchTotalAsync(AppHarness app, string word, string? token, CancellationToken ct)
    {
        var res = await SiteAsync(app, HttpMethod.Get, $"/api/find/search?q={word}", token, ct);
        return res.IsSuccessStatusCode ? (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("total").GetInt32() : -1;
    }

    private async Task<string> TagsAsync(AppHarness app, string? token, CancellationToken ct) =>
        await (await SiteAsync(app, HttpMethod.Get, "/api/tags", token, ct)).Content.ReadAsStringAsync(ct);

    private static Task<HttpResponseMessage> AdminAsync(AppHarness app, HttpMethod method, string path, CancellationToken ct, object? body = null) =>
        app.Admin.SendAsync(AppHarness.Req(method, "/api/admin/plugins/users" + path, app.Owner, app.Tenant, body: body), ct);

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

    private static async Task PollAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException("The condition was not met within 25 seconds.");
            }
            await Task.Delay(300, ct);
        }
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.Should().Be(expected, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
