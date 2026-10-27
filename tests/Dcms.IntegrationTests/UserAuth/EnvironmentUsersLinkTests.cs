using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.DynamicApps;
using Dcms.Plugins.UserAuth;
using Dcms.Shared.Data.Edge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.UserAuth;

/// <summary>
/// A users link (ADR 0026): development's sites sign people in through production's realm. Its
/// hosts stay development's, naming production's realm; production's realm takes them as redirect
/// hosts; content-api in development accepts production's tokens and no longer its own; and what
/// those people may do there is still development's roles and grants.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class EnvironmentUsersLinkTests(ContentFlowFixture fixture)
{
    private const string Deals = "dynamic-apps:crm:table:deals:read";

    [DockerFact]
    public async Task Development_signs_people_in_through_productions_users()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.InstallAsync("user-auth", "users", "{}", ct);
        var prodId = await TenantIdAsync(app.Tenant, ct);
        await Ok(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/environments/enable", app.Owner, app.Tenant), ct), ct);
        var devId = (await Ok(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/environments", app.Owner, app.Tenant,
            body: new { name = "development", empty = true }), ct), ct)).GetProperty("tenantId").GetGuid();
        var dev = $"{app.Tenant}-development";
        foreach (var (pluginId, slug) in new[] { ("dynamic-apps", "crm"), ("user-auth", "users") })
        {
            await Ok(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/plugins/instances", app.Owner, dev,
                body: new { pluginId, slug, name = slug, config = "{}" }), ct), ct);
        }
        var host = $"{dev}.example.test";
        await ExecuteAsync($$"""
            INSERT INTO tenancy.domains ("Id","TenantId","EnvironmentId","Hostname","VerificationToken","VerifiedAt","IsPrimary","SiteId","CreatedAt")
            VALUES ('{{Guid.NewGuid()}}','{{devId}}','{{devId}}','{{host}}','t',now(),true,'{{Guid.NewGuid()}}',now());
            """, ct);

        // Production's people, given a role in development by development.
        var pat = fixture.Realms.AddUser(prodId, "pat@corp.test");
        var role = (await Ok(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/plugins/users/roles", app.Owner, dev,
            body: new { key = "sales", name = "Sales", permissions = new[] { Deals } }), ct), ct)).GetProperty("id").GetGuid();
        await Ok(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/plugins/users/roles/{role}/grants", app.Owner, dev,
            body: new { subjectType = "user", subjectId = pat }), ct), ct);

        await Ok(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/environments/{devId}/links", app.Owner, app.Tenant,
            body: new { kind = "users", targetTenantId = prodId }), ct), ct);
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/environments/{prodId}/links", app.Owner, app.Tenant,
            body: new { kind = "users", targetTenantId = devId }), ct)).StatusCode.Should().Be(HttpStatusCode.Conflict, "never a chain");

        (await PublishAsync(devId, ct)).Should().BeTrue();
        var row = (await RowsAsync(devId, ct)).Single();
        (row.Hostname, row.RealmSlug, row.RealmTenantId).Should().Be((host, app.Tenant, prodId));
        fixture.Realms.RealmOf(prodId)!.Hosts.Should().Contain(host, "production's realm sends people back to development's sites");
        fixture.Realms.RealmOf(devId)!.Hosts.Should().BeEmpty();

        (await CheckAsync(dev, RealmTokens.Mint(prodId, pat), ct)).Should().BeTrue("production's user, development's grant");
        (await CheckAsync(dev, RealmTokens.Mint(devId, pat), ct)).Should().BeFalse("development's own realm no longer signs anyone in here");

        // Development reads production's people, and changes none of them.
        var users = await Ok(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/plugins/users/users", app.Owner, dev), ct), ct);
        users.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("email").GetString()).Should().Contain("pat@corp.test");
        var refused = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/plugins/users/groups", app.Owner, dev,
            body: new { name = "Staff" }), ct);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, $"/api/admin/plugins/users/users/{pat}/preview-token", app.Owner, dev,
            body: new { }), ct)).StatusCode.Should().Be(HttpStatusCode.Conflict, "a token production would accept, for someone nobody there let this member be");

        // Unlinked: development's hosts are its own realm's again.
        var link = (await Ok(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, $"/api/admin/environments/{devId}/links", app.Owner, app.Tenant), ct), ct))
            .EnumerateArray().Single().GetProperty("id").GetGuid();
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Delete, $"/api/admin/environments/{devId}/links/{link}", app.Owner, app.Tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await PublishAsync(devId, ct)).Should().BeTrue();
        await PublishAsync(prodId, ct);
        row = (await RowsAsync(devId, ct)).Single();
        (row.RealmSlug, row.RealmTenantId).Should().Be((dev, null));
        fixture.Realms.RealmOf(devId)!.Hosts.Should().Equal(host);
        fixture.Realms.RealmOf(prodId)!.Hosts.Should().NotContain(host);
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        res.IsSuccessStatusCode.Should().BeTrue(text);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<bool> CheckAsync(string tenant, string token, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/users/_contracts/users.access@1/Check") { Content = JsonContent.Create(new { permission = Deals }) };
        req.Headers.Add("X-Dcms-Tenant", tenant);
        req.Headers.Add("X-Dcms-Realm-Token", token);
        var res = await fixture.Content.CreateClient().SendAsync(req, ct);
        return (await Ok(res, ct)).GetProperty("allowed").GetBoolean();
    }

    private async Task<bool> PublishAsync(Guid tenantId, CancellationToken ct)
    {
        using var scope = fixture.Admin.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SiteGatePublisher>().PublishAsync(tenantId, ct, announce: false);
    }

    private async Task<List<EdgeSiteGate>> RowsAsync(Guid tenantId, CancellationToken ct)
    {
        using var scope = fixture.Admin.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EdgeDbContext>().SiteGates.AsNoTracking().Where(r => r.TenantId == tenantId).ToListAsync(ct);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<Guid> TenantIdAsync(string slug, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @p""";
        cmd.Parameters.AddWithValue("p", slug);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
