using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>A tenant with one Dynamic Apps instance (<c>crm</c>), and the requests the tests send to it.</summary>
public sealed class AppHarness(ContentFlowFixture fixture, HttpClient admin, Guid owner, string tenant)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    public HttpClient Admin => admin;
    public Guid Owner => owner;
    public string Tenant => tenant;

    public static async Task<AppHarness> CreateAsync(ContentFlowFixture fixture, CancellationToken ct)
    {
        var admin = fixture.Admin.CreateClient();
        var owner = Guid.NewGuid();
        var tenant = "da-" + Guid.NewGuid().ToString("N")[..8];
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug = tenant, name = tenant, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var installed = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            body: new { pluginId = "dynamic-apps", slug = "crm", name = "CRM", config = """{"displayName":"Sales CRM"}""" }), ct);
        installed.StatusCode.Should().Be(HttpStatusCode.Created, await installed.Content.ReadAsStringAsync(ct));
        return new AppHarness(fixture, admin, owner, tenant);
    }

    /// <summary>A request to <c>/api/admin/plugins/crm{path}</c> as <paramref name="as"/> (the owner by default).</summary>
    public HttpRequestMessage App(HttpMethod method, string path, object? body = null, Guid? @as = null) =>
        Req(method, "/api/admin/plugins/crm" + path, @as ?? owner, tenant, body: body);

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, Guid? @as = null) =>
        admin.SendAsync(App(method, path, body, @as), ct);

    public async Task<JsonElement> JsonAsync(HttpMethod method, string path, CancellationToken ct, object? body = null,
        HttpStatusCode expect = HttpStatusCode.OK, Guid? @as = null)
    {
        var res = await SendAsync(method, path, body, ct, @as);
        res.StatusCode.Should().Be(expect, await res.Content.ReadAsStringAsync(ct));
        return res.Content.Headers.ContentLength == 0 ? default : await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public static object Operations(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>Applies a change set to the current draft (opening one) and publishes it.</summary>
    public async Task<JsonElement> PublishAsync(string operations, CancellationToken ct)
    {
        var hash = (await JsonAsync(HttpMethod.Get, "/_model", ct)).GetProperty("hash").GetString();
        var applied = await JsonAsync(HttpMethod.Post, "/_model/draft/changes", ct, new { expectedHash = hash, operations = Operations(operations) });
        var draftHash = applied.GetProperty("draft").GetProperty("hash").GetString();
        return await JsonAsync(HttpMethod.Post, "/_model/draft/publish", ct, new { expectedHash = draftHash });
    }

    public async Task<Guid> AddMemberAsync(string[] permissions, CancellationToken ct)
    {
        var roleRes = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/roles", owner, tenant,
            body: new { name = "Limited " + Guid.NewGuid().ToString("N")[..6], permissions }), ct);
        roleRes.StatusCode.Should().Be(HttpStatusCode.Created, await roleRes.Content.ReadAsStringAsync(ct));
        var roleId = (await roleRes.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        var member = Guid.NewGuid();
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenancy.tenant_memberships ("Id", "TenantId", "UserId", "Email", "CreatedAt")
            VALUES (@mid, (SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @slug), @uid, @email, now());
            INSERT INTO tenancy.member_roles ("Id", "TenantId", "MembershipId", "TenantRoleId")
            VALUES (@mrid, (SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @slug), @mid, @rid);
            """;
        cmd.Parameters.AddWithValue("mid", Guid.NewGuid());
        cmd.Parameters.AddWithValue("slug", tenant);
        cmd.Parameters.AddWithValue("uid", member);
        cmd.Parameters.AddWithValue("email", $"{member:N}@dcms.test");
        cmd.Parameters.AddWithValue("mrid", Guid.NewGuid());
        cmd.Parameters.AddWithValue("rid", roleId);
        await cmd.ExecuteNonQueryAsync(ct);
        return member;
    }

    public static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string slug, string roles = "", object? body = null)
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
