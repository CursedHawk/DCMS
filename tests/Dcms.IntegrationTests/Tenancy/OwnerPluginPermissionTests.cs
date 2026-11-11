extern alias AdminApiApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdminApiApp::Dcms.AdminApi.Tenancy;
using Dcms.IntegrationTests.Cms;
using Dcms.IntegrationTests.DynamicApps;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dcms.IntegrationTests.Tenancy;

/// <summary>
/// Plugin permissions are held per instance (<c>plugin:{plugin}:{slug}:{action}</c>): two apps of
/// one plugin are granted separately, every check asks about the instance it is about, Owner gets
/// every action on each new instance, and a role holding a plugin-wide key from before holds it on
/// every instance after the startup conversion.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class OwnerPluginPermissionTests(ContentFlowFixture fixture)
{
    [DockerFact]
    public async Task Plugin_permissions_are_held_and_checked_per_instance()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await AppHarness.CreateAsync(fixture, ct); // Dynamic Apps "crm"
        await app.InstallAsync("dynamic-apps", "hr", """{"displayName":"HR"}""", ct);

        // Owner holds every action on both, and nothing plugin-wide.
        var owner = await PermissionsAsync(app, app.Owner, ct);
        owner.Should().Contain(["plugin:dynamic-apps:crm:model-read", "plugin:dynamic-apps:hr:data-delete"])
            .And.NotContain("plugin:dynamic-apps:model-read");

        // The role editor offers each instance's own keys, grouped by instance.
        var catalog = await JsonAsync(await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, "/api/admin/permissions/catalog", app.Owner, app.Tenant), ct), ct);
        var keys = catalog.EnumerateArray().Select(e => e.GetProperty("key").GetString()!).ToList();
        keys.Should().Contain(["plugin:dynamic-apps:crm:data-read", "plugin:dynamic-apps:hr:data-read"])
            .And.NotContain("plugin:dynamic-apps:data-read").And.NotContain(k => k.StartsWith("plugin:youtube:"));

        // A member granted one app reaches that app only: its routes, its contracts, its screens.
        var member = await app.AddMemberAsync(["plugin:dynamic-apps:crm:model-read"], ct);
        (await Get(app, "/api/admin/plugins/crm/_model", member, ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Get(app, "/api/admin/plugins/hr/_model", member, ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var contracts = await JsonAsync(await Get(app, "/api/admin/contracts?plane=admin", member, ct), ct);
        var config = contracts.EnumerateArray().Where(c => c.GetProperty("id").GetString() == "dynamic-apps.config@1").ToList();
        config.SelectMany(c => c.GetProperty("instances").EnumerateArray().Select(i => i.GetProperty("slug").GetString())).Should().Equal("crm");
        (await app.Admin.SendAsync(AppHarness.Req(HttpMethod.Post, "/api/admin/contracts/dynamic-apps.config@1/ListRevisions?instance=hr&plane=admin",
            member, app.Tenant, body: new { }), ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var nav = await (await Get(app, "/api/admin/navigation", member, ct)).Content.ReadAsStringAsync(ct);
        nav.Should().Contain("\"/apps/crm\"").And.NotContain("/apps/hr");

        // A role from before, holding a plugin-wide key, holds it on every instance after the conversion.
        var legacy = await app.AddMemberAsync([], ct);
        await GrantRawAsync(app, legacy, "plugin:dynamic-apps:data-read", ct);
        using (var scope = fixture.Admin.Services.CreateScope())
        {
            await OwnerPermissionBackfill.ApplyAsync(
                scope.ServiceProvider.GetRequiredService<TenancyDbContext>(), scope.ServiceProvider.GetRequiredService<CmsDbContext>(),
                scope.ServiceProvider.GetRequiredService<IPluginCatalog>(), scope.ServiceProvider.GetRequiredService<IAuditRecorder>(),
                scope.ServiceProvider.GetRequiredService<Dcms.Shared.Caching.ICacheService>(), NullLogger.Instance, ct);
        }
        var converted = await HeldByRoleOfAsync(legacy, ct);
        converted.Should().BeEquivalentTo(["plugin:dynamic-apps:crm:data-read", "plugin:dynamic-apps:hr:data-read"]);
    }

    private static Task<HttpResponseMessage> Get(AppHarness app, string url, Guid user, CancellationToken ct) =>
        app.Admin.SendAsync(AppHarness.Req(HttpMethod.Get, url, user, app.Tenant), ct);

    private static async Task<List<string>> PermissionsAsync(AppHarness app, Guid user, CancellationToken ct) =>
        (await JsonAsync(await Get(app, "/api/admin/me/permissions", user, ct), ct))
        .GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        response.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task GrantRawAsync(AppHarness app, Guid member, string permission, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenancy.tenant_role_permissions ("Id", "TenantId", "EnvironmentId", "TenantRoleId", "Permission")
            SELECT gen_random_uuid(), r."TenantId", r."EnvironmentId", r."Id", @p
            FROM tenancy.tenant_roles r
            JOIN tenancy.member_roles mr ON mr."TenantRoleId" = r."Id"
            JOIN tenancy.tenant_memberships m ON m."Id" = mr."MembershipId"
            WHERE m."UserId" = @u
            """;
        cmd.Parameters.AddWithValue("p", permission);
        cmd.Parameters.AddWithValue("u", member);
        (await cmd.ExecuteNonQueryAsync(ct)).Should().Be(1);
    }

    private async Task<List<string>> HeldByRoleOfAsync(Guid member, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p."Permission" FROM tenancy.tenant_role_permissions p
            JOIN tenancy.member_roles mr ON mr."TenantRoleId" = p."TenantRoleId"
            JOIN tenancy.tenant_memberships m ON m."Id" = mr."MembershipId"
            WHERE m."UserId" = @u
            """;
        cmd.Parameters.AddWithValue("u", member);
        var held = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) held.Add(reader.GetString(0));
        return held;
    }
}
