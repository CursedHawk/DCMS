using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// Plugin data sets over HTTP (ADR 0018), through VisitorAuth's "visitors": the runtime gates
/// on the set's own permissions, cuts values down to the item schema and validates them, keeps
/// filters to declared options, and audits every change against the instance.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class PluginDataSetEndpointsTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private const string Config = """
        {"attributes":[
          {"key":"phone","label":"Phone"},
          {"key":"tier","label":"Tier","type":"select","options":["gold","silver"],"visibility":"plugins"}
        ]}
        """;

    [DockerFact]
    public async Task Owner_browses_edits_actions_and_deletes_visitors()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, owner) = await SiteAsync(ct);
        var content = fixture.Content.CreateClient();
        await RegisterAsync(content, tenant, "eve@site.test", ct);
        await RegisterAsync(content, tenant, "max@site.test", ct);
        var admin = fixture.Admin.CreateClient();

        var sets = await JsonAsync(admin, AdminReq(HttpMethod.Get, "/api/admin/plugins/members/_data", owner, tenant), ct);
        var visitors = sets.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "visitors");
        visitors.GetProperty("canWrite").GetBoolean().Should().BeTrue();
        visitors.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("key").GetString())
            .Should().Contain(["email", "attributes.tier"], "columns follow the attribute definitions in the config");

        var found = await JsonAsync(admin, AdminReq(HttpMethod.Get, "/api/admin/plugins/members/_data/visitors?search=eve", owner, tenant), ct);
        found.GetProperty("total").GetInt64().Should().Be(1);
        var eve = found.GetProperty("rows")[0].GetProperty("key").GetString()!;

        var saved = await admin.SendAsync(AdminReq(HttpMethod.Put, $"/api/admin/plugins/members/_data/visitors/row?key={eve}", owner, tenant,
            body: new { displayName = "Eve", attributes = new { phone = "123", tier = "gold" }, email = "hijack@site.test" }), ct);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(ct));
        var row = (await saved.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("values");
        row.GetProperty("attributes").GetProperty("phone").GetString().Should().Be("123", "admins edit private attributes too");
        row.GetProperty("email").GetString().Should().Be("eve@site.test", "a value outside the item schema never reaches the plugin");

        var invalid = await admin.SendAsync(AdminReq(HttpMethod.Put, $"/api/admin/plugins/members/_data/visitors/row?key={eve}", owner, tenant,
            body: new { attributes = new { tier = "platinum" } }), ct);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var verified = await JsonAsync(admin, AdminReq(HttpMethod.Post, "/api/admin/plugins/members/_data/visitors/actions/verify", owner, tenant,
            body: new { keys = new[] { eve } }), ct);
        verified.GetProperty("affected").GetInt32().Should().Be(1);
        (await JsonAsync(admin, AdminReq(HttpMethod.Get, "/api/admin/plugins/members/_data/visitors?f.verified=yes", owner, tenant), ct))
            .GetProperty("total").GetInt64().Should().Be(1);
        (await JsonAsync(admin, AdminReq(HttpMethod.Get, "/api/admin/plugins/members/_data/visitors?f.verified=bogus", owner, tenant), ct))
            .GetProperty("total").GetInt64().Should().Be(2, "an option the set did not declare is ignored");

        (await admin.SendAsync(AdminReq(HttpMethod.Delete, $"/api/admin/plugins/members/_data/visitors/row?key={eve}", owner, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.SendAsync(AdminReq(HttpMethod.Get, $"/api/admin/plugins/members/_data/visitors/row?key={eve}", owner, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var audited = false;
        for (var i = 0; i < 40 && !audited; i++)
        {
            using var scope = fixture.Admin.Services.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<Dcms.Shared.Data.Audit.AuditDbContext>();
            using var rls = Dcms.Shared.Data.Rls.RlsScope.Platform();
            var actions = await audit.Events.AsNoTracking()
                .Where(e => e.Action.StartsWith("plugin.data.") && e.ResourceType == "plugin_instance")
                .Select(e => e.Action).ToListAsync(ct);
            audited = actions.Contains("plugin.data.updated") && actions.Contains("plugin.data.actioned") && actions.Contains("plugin.data.deleted");
            if (!audited) await Task.Delay(250, ct);
        }
        audited.Should().BeTrue("every change through a data set is recorded against the instance");
    }

    [DockerFact]
    public async Task A_set_is_gated_on_its_own_permissions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, owner) = await SiteAsync(ct);
        await RegisterAsync(fixture.Content.CreateClient(), tenant, "ada@site.test", ct);
        var admin = fixture.Admin.CreateClient();

        var reader = await AddMemberAsync(admin, owner, tenant, ["plugin:visitor-auth:read"], ct);
        var page = await JsonAsync(admin, AdminReq(HttpMethod.Get, "/api/admin/plugins/members/_data/visitors", reader, tenant), ct);
        var key = page.GetProperty("rows")[0].GetProperty("key").GetString();
        var sets = await JsonAsync(admin, AdminReq(HttpMethod.Get, "/api/admin/plugins/members/_data", reader, tenant), ct);
        sets[0].GetProperty("canWrite").GetBoolean().Should().BeFalse();
        (await admin.SendAsync(AdminReq(HttpMethod.Put, $"/api/admin/plugins/members/_data/visitors/row?key={key}", reader, tenant,
            body: new { displayName = "x" }), ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var outsider = await AddMemberAsync(admin, owner, tenant, ["site:edit"], ct);
        (await JsonAsync(admin, AdminReq(HttpMethod.Get, "/api/admin/plugins/members/_data", outsider, tenant), ct))
            .GetArrayLength().Should().Be(0, "a set the caller may not read is not listed");
        (await admin.SendAsync(AdminReq(HttpMethod.Get, "/api/admin/plugins/members/_data/visitors", outsider, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.SendAsync(AdminReq(HttpMethod.Get, "/api/admin/plugins/nosuch/_data", owner, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task A_switched_off_plugins_data_stays_browsable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, owner) = await SiteAsync(ct);
        await RegisterAsync(fixture.Content.CreateClient(), tenant, "zoe@site.test", ct);
        var admin = fixture.Admin.CreateClient();
        var instances = await JsonAsync(admin, AdminReq(HttpMethod.Get, "/api/admin/plugins/instances", owner, tenant), ct);
        var id = instances.EnumerateArray().Single(i => i.GetProperty("slug").GetString() == "members").GetProperty("id").GetGuid();
        (await admin.SendAsync(AdminReq(HttpMethod.Post, $"/api/admin/plugins/instances/{id}/disable", owner, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var page = await JsonAsync(admin, AdminReq(HttpMethod.Get, "/api/admin/plugins/members/_data/visitors", owner, tenant), ct);
        page.GetProperty("total").GetInt64().Should().Be(1, "switching a plugin off hides it from sites, not its data from its owners");
    }

    // ---- helpers ----

    private static async Task<JsonElement> JsonAsync(HttpClient client, HttpRequestMessage req, CancellationToken ct)
    {
        var res = await client.SendAsync(req, ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task<Guid> AddMemberAsync(HttpClient admin, Guid owner, string tenant, string[] permissions, CancellationToken ct)
    {
        var roleRes = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/roles", owner, tenant,
            body: new { name = "Limited " + Guid.NewGuid().ToString("N")[..6], permissions }), ct);
        roleRes.StatusCode.Should().Be(HttpStatusCode.Created, await roleRes.Content.ReadAsStringAsync(ct));
        var roleId = (await roleRes.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        var member = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenancy.tenant_memberships ("Id", "TenantId", "UserId", "Email", "CreatedAt")
            VALUES (@mid, (SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @slug), @uid, @email, now());
            INSERT INTO tenancy.member_roles ("Id", "TenantId", "MembershipId", "TenantRoleId")
            VALUES (@mrid, (SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @slug), @mid, @rid);
            """;
        cmd.Parameters.AddWithValue("mid", membershipId);
        cmd.Parameters.AddWithValue("slug", tenant);
        cmd.Parameters.AddWithValue("uid", member);
        cmd.Parameters.AddWithValue("email", $"{member:N}@dcms.test");
        cmd.Parameters.AddWithValue("mrid", Guid.NewGuid());
        cmd.Parameters.AddWithValue("rid", roleId);
        await cmd.ExecuteNonQueryAsync(ct);
        return member;
    }

    private async Task<(string Slug, Guid Owner)> SiteAsync(CancellationToken ct)
    {
        var admin = fixture.Admin.CreateClient();
        var owner = Guid.NewGuid();
        var slug = "pds-" + Guid.NewGuid().ToString("N")[..8];
        (await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct)).StatusCode.Should().Be(HttpStatusCode.Created);
        var res = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/plugins/instances", owner, slug,
            body: new { pluginId = "visitor-auth", slug = "members", name = "Members", config = Config }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync(ct));
        return (slug, owner);
    }

    private static async Task RegisterAsync(HttpClient content, string tenant, string email, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/members/register")
        {
            Content = JsonContent.Create(new { email, password = "correct horse battery staple" }),
        };
        req.Headers.Add("X-Dcms-Tenant", tenant);
        (await content.SendAsync(req, ct)).EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage AdminReq(HttpMethod method, string url, Guid sub, string slug, string roles = "", object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (!string.IsNullOrEmpty(roles)) req.Headers.Add("X-Test-Roles", roles);
        if (slug.Length > 0) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }
}
