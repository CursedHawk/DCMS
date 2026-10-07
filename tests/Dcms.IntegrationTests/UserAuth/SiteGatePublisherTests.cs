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
/// What the edge is told about a tenant's sites (ADR 0022, UA3): a row per verified hostname of a
/// site with the plugin enabled, carrying that site's rules in order and the tenant's realm; kept
/// in step as rules change; gone when the plugin is.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class SiteGatePublisherTests(ContentFlowFixture fixture)
{
    [DockerFact]
    public async Task A_sites_hostnames_carry_its_rules_and_lose_them_with_the_plugin()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct);
        await app.InstallAsync("user-auth", "users", "{}", ct);
        var tenantId = await ScalarAsync<Guid>("""SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @p""", app.Tenant, ct);
        var site = Guid.NewGuid();
        var other = Guid.NewGuid();
        var staff = Guid.NewGuid();
        var host = $"{app.Tenant}.example.test";
        await ExecuteAsync($$"""
            INSERT INTO tenancy.domains ("Id","TenantId","Hostname","VerificationToken","VerifiedAt","IsPrimary","SiteId","CreatedAt") VALUES
              ('{{Guid.NewGuid()}}','{{tenantId}}','{{host}}','t',now(),true,'{{site}}',now()),
              ('{{Guid.NewGuid()}}','{{tenantId}}','unverified.{{host}}','t',NULL,false,'{{site}}',now()),
              ('{{Guid.NewGuid()}}','{{tenantId}}','other.{{host}}','t',now(),false,'{{other}}',now());
            INSERT INTO userauth.gates ("Id","TenantId","SiteId","Position","PathPrefix","Access","Groups","CreatedAt","UpdatedAt") VALUES
              ('{{Guid.NewGuid()}}','{{tenantId}}','{{site}}',2,'/members','SignedIn','{}',now(),now()),
              ('{{Guid.NewGuid()}}','{{tenantId}}','{{site}}',1,'/portal','Groups','{{{staff}}}',now(),now());
            """, ct);

        (await PublishAsync(tenantId, ct)).Should().BeTrue();
        var rows = await RowsAsync(tenantId, ct);
        rows.Select(r => r.Hostname).Should().BeEquivalentTo([host, $"other.{host}"], "verified hostnames bound to a site, each one");
        var gated = rows.Single(r => r.Hostname == host);
        gated.RealmSlug.Should().Be(app.Tenant);
        var rules = JsonDocument.Parse(gated.RulesJson).RootElement.EnumerateArray().ToList();
        rules.Select(r => r.GetProperty("prefix").GetString()).Should().Equal("/portal", "/members");
        rules[0].GetProperty("access").GetString().Should().Be("groups");
        rules[0].GetProperty("groups")[0].GetGuid().Should().Be(staff);
        JsonDocument.Parse(rows.Single(r => r.Hostname != host).RulesJson).RootElement.GetArrayLength()
            .Should().Be(0, "a site without rules is still the tenant's: its pages can offer sign-in");

        (await PublishAsync(tenantId, ct)).Should().BeFalse("nothing changed, so the edge is not told anything");

        // The plugin goes: so does everything the edge enforces for this tenant.
        var disabled = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post,
            $"/api/admin/plugins/instances/{await InstanceIdAsync(app, ct)}/disable", app.Owner, app.Tenant), ct);
        disabled.StatusCode.Should().Be(HttpStatusCode.NoContent, await disabled.Content.ReadAsStringAsync(ct));
        (await PublishAsync(tenantId, ct)).Should().BeTrue();
        (await RowsAsync(tenantId, ct)).Should().BeEmpty();
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

    private static async Task<Guid> InstanceIdAsync(AppHarness app, CancellationToken ct)
    {
        var list = await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/plugins/instances", app.Owner, app.Tenant), ct);
        return (await list.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray()
            .Single(i => i.GetProperty("pluginId").GetString() == "user-auth").GetProperty("id").GetGuid();
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<T> ScalarAsync<T>(string sql, string parameter, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("p", parameter);
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
