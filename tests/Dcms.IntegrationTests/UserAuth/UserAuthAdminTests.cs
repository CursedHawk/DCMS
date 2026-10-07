using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.DynamicApps;
using Dcms.Shared.Data.Edge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.UserAuth;

/// <summary>
/// The User Authentication console (ADR 0022, UA4): the realm and its directory passed through to
/// identity under the member's permissions, and roles, grants and site rules in the plugin's own
/// store — the rules reaching the edge as they are saved.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class UserAuthAdminTests(ContentFlowFixture fixture)
{
    [DockerFact]
    public async Task The_realm_is_the_tenants_and_its_directory_goes_through_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);

        var realm = await JsonAsync(await SendAsync(app, HttpMethod.Get, "/realm", ct), HttpStatusCode.OK, ct);
        realm.GetProperty("slug").GetString().Should().Be(app.Tenant, "created on first sight, named after the tenant");
        fixture.Realms.RealmOf(tenantId).Should().NotBeNull();

        var invited = await JsonAsync(await SendAsync(app, HttpMethod.Post, "/users/invite", ct, new { email = "pat@corp.test" }), HttpStatusCode.OK, ct);
        invited.GetProperty("user").GetProperty("status").GetString().Should().Be("invited");
        var refused = await SendAsync(app, HttpMethod.Post, "/users/invite", ct, new { email = "pat@corp.test" });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, "identity's refusal reaches the console as identity gave it");
        (await refused.Content.ReadAsStringAsync(ct)).Should().Contain("already has an account");

        var users = await JsonAsync(await SendAsync(app, HttpMethod.Get, "/users", ct), HttpStatusCode.OK, ct);
        users.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("email").GetString()).Should().Equal("pat@corp.test");

        // A member without the plugin's permissions reaches none of it.
        var outsider = await app.AddMemberAsync([], ct);
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/plugins/users/users", outsider, app.Tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var reader = await app.AddMemberAsync(["plugin:user-auth:users-read"], ct);
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/plugins/users/users", reader, app.Tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/plugins/users/users/invite", reader, app.Tenant,
                body: new { email = "sam@corp.test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "reading the directory is not changing it");
    }

    [DockerFact]
    public async Task Roles_hold_site_permissions_and_go_to_groups_and_users()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, _) = await SetUpAsync(ct);

        (await SendAsync(app, HttpMethod.Post, "/roles", ct, new { key = "sales", name = "Sales", permissions = new[] { "not a permission" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(app, HttpMethod.Post, "/roles", ct, new { key = "Sales Team", name = "Sales" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "keys are snake_case");
        var role = await JsonAsync(await SendAsync(app, HttpMethod.Post, "/roles", ct,
            new { key = "sales", name = "Sales", permissions = new[] { "dynamic-apps:crm:table:deals:read", "dynamic-apps:crm:table:deals:read" } }),
            HttpStatusCode.Created, ct);
        role.GetProperty("permissions").GetArrayLength().Should().Be(1, "duplicates collapse");
        (await SendAsync(app, HttpMethod.Post, "/roles", ct, new { key = "sales", name = "Again" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        var group = (await JsonAsync(await SendAsync(app, HttpMethod.Post, "/groups", ct, new { name = "Staff" }), HttpStatusCode.Created, ct))
            .GetProperty("id").GetGuid();
        var roleId = role.GetProperty("id").GetGuid();
        (await SendAsync(app, HttpMethod.Post, $"/roles/{roleId}/grants", ct, new { subjectType = "group", subjectId = group }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await SendAsync(app, HttpMethod.Post, $"/roles/{roleId}/grants", ct, new { subjectType = "group", subjectId = group }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "granting twice is the same grant");
        (await SendAsync(app, HttpMethod.Post, $"/roles/{roleId}/grants", ct, new { subjectType = "team", subjectId = group }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var roles = await JsonAsync(await SendAsync(app, HttpMethod.Get, "/roles", ct), HttpStatusCode.OK, ct);
        roles[0].GetProperty("grants")[0].GetProperty("subjectType").GetString().Should().Be("group");

        // The group goes at identity: so do the grants to it.
        (await SendAsync(app, HttpMethod.Delete, $"/groups/{group}", ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        roles = await JsonAsync(await SendAsync(app, HttpMethod.Get, "/roles", ct), HttpStatusCode.OK, ct);
        roles[0].GetProperty("grants").GetArrayLength().Should().Be(0);
    }

    [DockerFact]
    public async Task A_sites_rules_are_saved_in_order_and_reach_the_edge()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, tenantId) = await SetUpAsync(ct);
        var site = await CreateSiteAsync(app, ct);
        var host = $"{app.Tenant}.example.test";
        await LinkDomainAsync(app, host, site, ct);
        var staff = Guid.NewGuid();

        foreach (var bad in new object[]
                 {
                     new[] { new { prefix = "portal", access = "signedIn", groups = (Guid[]?)null } },
                     new[] { new { prefix = "/a%2Fb", access = "signedIn", groups = (Guid[]?)null } },
                     new[] { new { prefix = "/a/../b", access = "signedIn", groups = (Guid[]?)null } },
                     new[] { new { prefix = "/portal", access = "groups", groups = (Guid[]?)null } },
                     new[] { new { prefix = "/portal", access = "everyone", groups = (Guid[]?)null } },
                     new[] { new { prefix = "/portal", access = "signedIn", groups = (Guid[]?)null }, new { prefix = "/Portal/", access = "public", groups = (Guid[]?)null } },
                 })
        {
            (await SendAsync(app, HttpMethod.Put, $"/sites/{site}/rules", ct, bad)).StatusCode
                .Should().Be(HttpStatusCode.BadRequest, JsonSerializer.Serialize(bad));
        }
        (await SendAsync(app, HttpMethod.Put, $"/sites/{Guid.NewGuid()}/rules", ct, Array.Empty<object>()))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "another tenant's site, or none, is not this tenant's to gate");

        var saved = await SendAsync(app, HttpMethod.Put, $"/sites/{site}/rules", ct, new object[]
        {
            new { prefix = "/portal/public/", access = "public" },
            new { prefix = "/portal", access = "groups", groups = new[] { staff } },
        });
        saved.StatusCode.Should().Be(HttpStatusCode.NoContent, await saved.Content.ReadAsStringAsync(ct));

        var row = await EdgeRowAsync(host, ct);
        var rules = JsonDocument.Parse(row!.RulesJson).RootElement.EnumerateArray().ToList();
        rules.Select(r => r.GetProperty("prefix").GetString()).Should().Equal("/portal/public", "/portal");
        rules[1].GetProperty("groups")[0].GetGuid().Should().Be(staff);
        fixture.Realms.RealmOf(tenantId)!.Hosts.Should().Equal([host], "identity redirects back only to the hosts the edge gates");

        var sites = await JsonAsync(await SendAsync(app, HttpMethod.Get, "/sites", ct), HttpStatusCode.OK, ct);
        var listed = sites.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == site);
        listed.GetProperty("rules").GetArrayLength().Should().Be(2);
        listed.GetProperty("hosts")[0].GetProperty("hostname").GetString().Should().Be(host);

        // Replaced as a whole: the same prefix again is fine.
        (await SendAsync(app, HttpMethod.Put, $"/sites/{site}/rules", ct, new object[] { new { prefix = "/portal", access = "signedIn" } }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        JsonDocument.Parse((await EdgeRowAsync(host, ct))!.RulesJson).RootElement.GetArrayLength().Should().Be(1);
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

    private static Task<HttpResponseMessage> SendAsync(AppHarness app, HttpMethod method, string path, CancellationToken ct, object? body = null) =>
        app.Admin.SendAsync(AppHarness.Req(method, "/api/admin/plugins/users" + path, app.Owner, app.Tenant, body: body), ct);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.Should().Be(expected, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<Guid> CreateSiteAsync(AppHarness app, CancellationToken ct)
    {
        var created = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/sites", app.Owner, app.Tenant,
            body: new { name = "Portal", renderMode = "StaticPrerender" }), ct);
        return (await JsonAsync(created, HttpStatusCode.Created, ct)).GetProperty("id").GetGuid();
    }

    private async Task LinkDomainAsync(AppHarness app, string host, Guid site, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenancy.domains ("Id","TenantId","Hostname","VerificationToken","VerifiedAt","IsPrimary","SiteId","CreatedAt")
            SELECT gen_random_uuid(), "Id"::uuid, @host, 't', now(), true, @site, now() FROM tenancy.tenants WHERE "Identifier" = @slug
            """;
        cmd.Parameters.AddWithValue("host", host);
        cmd.Parameters.AddWithValue("site", site);
        cmd.Parameters.AddWithValue("slug", app.Tenant);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<EdgeSiteGate?> EdgeRowAsync(string host, CancellationToken ct)
    {
        using var scope = fixture.Admin.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EdgeDbContext>().SiteGates.AsNoTracking().FirstOrDefaultAsync(r => r.Hostname == host, ct);
    }
}
