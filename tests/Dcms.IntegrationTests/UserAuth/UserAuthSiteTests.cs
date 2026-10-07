using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.DynamicApps;

namespace Dcms.IntegrationTests.UserAuth;

/// <summary>
/// Enterprise users on the site plane (ADR 0022, UA4): the realm token the edge forwards is
/// recognised by content-api for its own tenant only, and what the user holds is the union of
/// the roles granted to them and to their groups.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class UserAuthSiteTests(ContentFlowFixture fixture)
{
    private const string Deals = "dynamic-apps:crm:table:deals:read";

    [DockerFact]
    public async Task A_users_permissions_come_from_their_groups_and_their_own_grants()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        var staff = Guid.NewGuid();
        var direct = Guid.NewGuid();
        var role = await RoleAsync(app, "sales", [Deals], ct);
        await GrantAsync(app, role, "group", staff, ct);
        var lead = await RoleAsync(app, "lead", ["dynamic-apps:crm:flow:approve:run"], ct);
        await GrantAsync(app, lead, "user", direct, ct);

        (await CheckAsync(app, RealmTokens.Mint(tenantId, Guid.NewGuid(), [staff]), Deals, ct)).Should().BeTrue("through the group");
        (await CheckAsync(app, RealmTokens.Mint(tenantId, Guid.NewGuid()), Deals, ct)).Should().BeFalse("not in the group");
        (await CheckAsync(app, null, Deals, ct)).Should().BeFalse("nobody signed in");

        var permissions = await CallAsync(app, RealmTokens.Mint(tenantId, direct, [staff]), "users.access@1/ListPermissions", null, ct);
        permissions.GetProperty("permissions").EnumerateArray().Select(p => p.GetString())
            .Should().Equal("dynamic-apps:crm:flow:approve:run", Deals);
    }

    [DockerFact]
    public async Task A_token_is_somebody_only_on_its_own_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        var staff = Guid.NewGuid();
        await GrantAsync(app, await RoleAsync(app, "sales", [Deals], ct), "group", staff, ct);
        var user = Guid.NewGuid();

        var me = await CallAsync(app, RealmTokens.Mint(tenantId, user, [staff]), "users.identity@1/GetCurrent", null, ct);
        me.GetProperty("user").GetProperty("id").GetGuid().Should().Be(user);
        me.GetProperty("user").GetProperty("groups")[0].GetGuid().Should().Be(staff);

        var other = Guid.NewGuid();
        foreach (var token in new[]
                 {
                     RealmTokens.Mint(other, user, [staff]),
                     RealmTokens.Mint(tenantId, user, [staff], audience: $"dcms.realm:{other}"),
                     RealmTokens.Mint(tenantId, user, [staff], realm: other.ToString()),
                     RealmTokens.Mint(tenantId, user, [staff], audience: $"dcms.site:{tenantId}"),
                     RealmTokens.Mint(tenantId, user, [staff], expires: DateTime.UtcNow.AddMinutes(-5)),
                 })
        {
            (await CallAsync(app, token, "users.identity@1/GetCurrent", null, ct)).GetProperty("user").ValueKind
                .Should().Be(JsonValueKind.Null);
            (await CheckAsync(app, token, Deals, ct)).Should().BeFalse();
        }

        // A realm token in the Authorization header is not a realm token: only the edge's header carries one.
        var res = await app.SiteAsync(HttpMethod.Post, "/api/users/_contracts/users.identity@1/GetCurrent", ct, new { },
            token: RealmTokens.Mint(tenantId, user, [staff]));
        (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("user").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [DockerFact]
    public async Task The_user_is_also_the_visitor_to_plugins_that_know_only_visitors()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        var user = Guid.NewGuid();
        var visitor = await CallAsync(app, RealmTokens.Mint(tenantId, user, email: "pat@corp.test"), "visitors.identity@1/GetCurrent", null, ct);
        visitor.GetProperty("visitor").GetProperty("id").GetGuid().Should().Be(user);
        visitor.GetProperty("visitor").GetProperty("email").GetString().Should().Be("pat@corp.test");
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

    private static async Task<Guid> RoleAsync(AppHarness app, string key, string[] permissions, CancellationToken ct)
    {
        var res = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/plugins/users/roles", app.Owner, app.Tenant,
            body: new { key, name = key, permissions }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync(ct));
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private static async Task GrantAsync(AppHarness app, Guid role, string subjectType, Guid subjectId, CancellationToken ct)
    {
        var res = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/plugins/users/roles/{role}/grants", app.Owner, app.Tenant,
            body: new { subjectType, subjectId }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync(ct));
    }

    private async Task<bool> CheckAsync(AppHarness app, string? token, string permission, CancellationToken ct) =>
        (await CallAsync(app, token, "users.access@1/Check", new { permission }, ct)).GetProperty("allowed").GetBoolean();

    /// <summary>A site-plane contract call on the user-auth instance, carrying the token as the edge would.</summary>
    private async Task<JsonElement> CallAsync(AppHarness app, string? token, string operation, object? body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/users/_contracts/{operation}") { Content = JsonContent.Create(body ?? new { }) };
        req.Headers.Add("X-Dcms-Tenant", app.Tenant);
        if (token is not null)
        {
            req.Headers.Add("X-Dcms-Realm-Token", token);
        }
        var res = await fixture.Content.CreateClient().SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
