using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;

namespace Dcms.IntegrationTests.DynamicApps;

/// <summary>A tenant with one Dynamic Apps instance (<c>crm</c>), and the requests the tests send to it.</summary>
public sealed class AppHarness(ContentFlowFixture fixture, HttpClient admin, Guid owner, string tenant, Guid instanceId)
{
    public Guid InstanceId => instanceId;

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
        var instanceId = (await installed.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        return new AppHarness(fixture, admin, owner, tenant, instanceId);
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

    /// <summary>Adds another plugin instance to the tenant.</summary>
    public async Task InstallAsync(string pluginId, string slug, string config, CancellationToken ct)
    {
        var res = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            body: new { pluginId, slug, name = slug, config }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>A public-site request to content-api, optionally as a signed-in visitor.</summary>
    public Task<HttpResponseMessage> SiteAsync(HttpMethod method, string url, CancellationToken ct, object? body = null, string? token = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Dcms-Tenant", tenant);
        if (token is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        return fixture.Content.CreateClient().SendAsync(req, ct);
    }

    /// <summary>Registers a site visitor with the tenant's VisitorAuth instance at <c>/api/{members}</c>; returns their access token.</summary>
    public async Task<string> VisitorAsync(string email, CancellationToken ct, string members = "members")
    {
        var res = await SiteAsync(HttpMethod.Post, $"/api/{members}/register", ct, new { email, password = "correct horse battery staple" });
        res.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.Created], await res.Content.ReadAsStringAsync(ct));
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString()!;
    }

    /// <summary>This tenant's outbox, oldest first: event name and envelope.</summary>
    public async Task<List<(string Name, JsonElement Envelope)>> OutboxAsync(CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "EventName", "Envelope"::text FROM apps.outbox
            WHERE "TenantId" = (SELECT "Id"::uuid FROM tenancy.tenants WHERE "Identifier" = @slug)
            ORDER BY "OccurredAt", "Id"
            """;
        cmd.Parameters.AddWithValue("slug", tenant);
        var rows = new List<(string, JsonElement)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetString(0), JsonDocument.Parse(reader.GetString(1)).RootElement.Clone()));
        }
        return rows;
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
