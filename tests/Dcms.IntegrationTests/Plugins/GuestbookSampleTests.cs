using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// The Guestbook sample, loaded from a folder as an operator would install it, against the real
/// hosts: everything an outside plugin can declare with the SDK alone has to work — its default
/// permission grant, site routes with a hook and throttling, admin routes gated by its own
/// permissions and audited under its own names, its data sets, its host route, its screens.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class GuestbookSampleTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task An_installed_plugin_works_end_to_end()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fixture.Admin.CreateClient();
        var content = fixture.Content.CreateClient();
        var owner = Guid.NewGuid();
        var tenant = "gb-" + Guid.NewGuid().ToString("N")[..8];
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug = tenant, name = tenant, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var installed = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            body: new
            {
                pluginId = "sample-guestbook", slug = "guests", name = "Guests", description = "Our guestbook",
                config = """{"moderation":"pre","blockedWords":["darn"],"accent":"teal"}""",
            }), ct);
        installed.StatusCode.Should().Be(HttpStatusCode.Created, await installed.Content.ReadAsStringAsync(ct));

        // GrantToMembers: the first guestbook gave the Member role `read`.
        var roles = await Json(admin, Req(HttpMethod.Get, "/api/admin/roles", owner, tenant), ct);
        roles.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Member")
            .GetProperty("permissions").EnumerateArray().Select(p => p.GetString())
            .Should().Contain("plugin:sample-guestbook:read");

        // Site route + its own hook: the blocked word is masked, the entry waits for approval.
        var signed = await Json(content, Site(HttpMethod.Post, tenant, "/api/guests/sign", new { name = "Ada", message = "What a darn fine site" }), ct);
        signed.GetProperty("needsApproval").GetBoolean().Should().BeTrue();
        signed.GetProperty("entry").GetProperty("message").GetString().Should().Be("What a **** fine site");
        var id = signed.GetProperty("entry").GetProperty("id").GetString()!;
        (await Json(content, Site(HttpMethod.Get, tenant, "/api/guests/entries"), ct)).GetProperty("total").GetInt64()
            .Should().Be(0, "a pre-moderated entry is not public yet");

        // Admin routes gated by the plugin's own permissions (RequirePluginPermission).
        (await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/guests/stats", owner, tenant), ct))
            .GetProperty("pending").GetInt64().Should().Be(1);
        var outsider = await AddMemberAsync(admin, owner, tenant, ["site:edit"], ct);
        (await admin.SendAsync(Req(HttpMethod.Get, "/api/admin/plugins/guests/stats", outsider, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/plugins/guests/entries/{id}/approve", outsider, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/plugins/guests/entries/{id}/approve", owner, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(content, Site(HttpMethod.Get, tenant, "/api/guests/entries"), ct)).GetProperty("total").GetInt64().Should().Be(1);

        // Data sets: its own, plus the platform's for the stores it uses.
        var sets = await Json(admin, Req(HttpMethod.Get, "/api/admin/plugins/guests/_data", owner, tenant), ct);
        sets.EnumerateArray().Select(s => s.GetProperty("id").GetString()).Should().Contain(["entries", "dcms.storage", "dcms.blobs"]);

        // Host route with the plugin's tenant-wide context.
        var overview = await Json(admin, Req(HttpMethod.Get, "/api/admin/guestbook/overview", owner, tenant), ct);
        overview.GetProperty("guestbooks").EnumerateArray().Single().GetProperty("approved").GetInt64().Should().Be(1);

        // Screens: its menu entry, and its UI module served from its folder.
        (await Json(admin, Req(HttpMethod.Get, "/api/admin/navigation", owner, tenant), ct))
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("to").GetString())
            .Should().Contain("/app/sample-guestbook/overview");
        var folder = Path.Combine(ContentFlowFixture.InstalledPluginsDirectory, "Dcms.Plugins.Sample.Guestbook", "admin");
        if (!File.Exists(Path.Combine(folder, "index.js")))
        {
            // The .NET-only CI job never builds the UI; stand in for its output.
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "index.js"), "export default {};", ct);
        }
        var ui = (await Json(admin, Req(HttpMethod.Get, "/api/admin/plugin-ui", owner, tenant), ct))
            .EnumerateArray().Single(p => p.GetProperty("pluginId").GetString() == "sample-guestbook");
        ui.GetProperty("source").GetString().Should().Be("installed");
        var url = ui.GetProperty("module").GetProperty("url").GetString()!;
        var asset = await admin.GetAsync(url, ct);
        asset.StatusCode.Should().Be(HttpStatusCode.OK, "an installed plugin's UI is served anonymously from its folder");
        asset.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
        // Encoded so the client sends it as is: the server must refuse to leave the admin folder.
        (await admin.GetAsync("/api/admin/plugin-ui/assets/sample-guestbook/%2E%2E%2FDcms.Plugins.Sample.Guestbook.dll", ct))
            .StatusCode.Should().NotBe(HttpStatusCode.OK, "nothing outside the plugin's admin folder is served");

        // Audited under the plugin's own name (AuditAs).
        var audited = false;
        for (var i = 0; i < 40 && !audited; i++)
        {
            using var scope = fixture.Admin.Services.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<Dcms.Shared.Data.Audit.AuditDbContext>();
            using var rls = Dcms.Shared.Data.Rls.RlsScope.Platform();
            audited = await audit.Events.AsNoTracking().AnyAsync(e => e.Action == "plugin.sample-guestbook.entry.approved", ct);
            if (!audited) await Task.Delay(250, ct);
        }
        audited.Should().BeTrue();

        // dcms.cache throttling: five signatures an hour from one address, then 429.
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 5; i++)
        {
            last = (await content.SendAsync(Site(HttpMethod.Post, tenant, "/api/guests/sign", new { name = "Max", message = $"Hello {i}" }), ct)).StatusCode;
        }
        last.Should().Be(HttpStatusCode.TooManyRequests);
    }

    // ---- helpers ----

    private static async Task<JsonElement> Json(HttpClient client, HttpRequestMessage req, CancellationToken ct)
    {
        var res = await client.SendAsync(req, ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task<Guid> AddMemberAsync(HttpClient admin, Guid owner, string tenant, string[] permissions, CancellationToken ct)
    {
        var roleRes = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/roles", owner, tenant,
            body: new { name = "Limited " + Guid.NewGuid().ToString("N")[..6], permissions }), ct);
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

    private static HttpRequestMessage Site(HttpMethod method, string tenant, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Dcms-Tenant", tenant);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string slug, string roles = "", object? body = null)
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
