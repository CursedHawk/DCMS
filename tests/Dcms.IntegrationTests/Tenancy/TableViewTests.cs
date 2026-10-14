using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Dcms.IntegrationTests.Tenancy;

/// <summary>
/// #278: saved table views are personal until their owner shares them — with members, roles or
/// the workspace — read-only; and a view never opens a table its holder could not.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class TableViewTests(AdminApiFixture fixture)
{
    private const string Table = "content:blog:post";

    private static readonly object Definition = new
    {
        columns = new[] { "title", "status", "price" },
        sort = new[] { new { field = "updatedAt", direction = "desc" } },
        filter = new
        {
            or = new object[]
            {
                new { field = "status", op = "eq", value = "draft" },
                new { and = new object[] { new { field = "price", op = "gte", value = 10 }, new { field = "title", op = "contains", value = "sale" } } },
            },
        },
    };

    [DockerFact]
    public async Task A_view_is_personal_until_shared_and_read_only_to_whoever_it_is_shared_with()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var owner = Guid.NewGuid();
        var (tenantId, slug) = await CreateTenantAsync(client, owner, ct);
        var colleague = await SeedMemberAsync(tenantId, "Owner", ct);
        var other = await SeedMemberAsync(tenantId, "Owner", ct);
        var reader = await SeedMemberAsync(tenantId, "Member", ct); // no content:read

        // Bad input is refused before anything is stored.
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/views", slug, owner,
            new { table = Table, name = "Bad", definition = new { filter = new { field = "x", op = "like", value = "a" } } }), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/views?table=nope", slug, owner, null), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/views?table=dataset:no-such-instance:rows", slug, owner, null), ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var created = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/views", slug, owner,
            new { table = Table, name = "Cheap drafts", definition = Definition }), ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
        var view = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
        var id = view.GetProperty("id").GetGuid();
        // The filter comes back as it went in: groups, operators, values.
        view.GetProperty("definition").GetProperty("filter").GetProperty("or")[1].GetProperty("and")[0]
            .GetProperty("value").GetInt32().Should().Be(10);

        (await ViewsAsync(client, slug, colleague, ct)).Should().BeEmpty("a new view is its owner's alone");

        // Shared with the Owner role: the colleague sees it, cannot change it, and can open with it.
        var ownerRole = await RoleIdAsync(tenantId, "Owner", ct);
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/views/{id}/sharing", slug, owner,
            new { workspace = false, userIds = Array.Empty<Guid>(), roleIds = new[] { ownerRole } }), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var seen = (await ViewsAsync(client, slug, colleague, ct)).Should().ContainSingle().Subject;
        seen.GetProperty("mine").GetBoolean().Should().BeFalse();
        seen.GetProperty("sharedWith").ValueKind.Should().Be(JsonValueKind.Null);
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/views/{id}", slug, colleague,
            new { name = "Mine now", definition = Definition }), ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.SendAsync(Req(HttpMethod.Put, "/api/admin/views/default", slug, colleague,
            new { table = Table, view = id.ToString() }), ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Shared with one person instead: the role loses it.
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/views/{id}/sharing", slug, owner,
            new { workspace = false, userIds = new[] { other }, roleIds = Array.Empty<Guid>() }), ct))
            .EnsureSuccessStatusCode();
        (await ViewsAsync(client, slug, other, ct)).Should().ContainSingle();
        (await ViewsAsync(client, slug, colleague, ct)).Should().BeEmpty();

        // With the workspace: everyone who can read the table — but not who cannot.
        (await client.SendAsync(Req(HttpMethod.Put, $"/api/admin/views/{id}/sharing", slug, owner,
            new { workspace = true }), ct)).EnsureSuccessStatusCode();
        (await ViewsAsync(client, slug, colleague, ct)).Should().ContainSingle();
        (await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/views?table={Table}", slug, reader, null), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Another workspace sees nothing of it, on the same table.
        var stranger = Guid.NewGuid();
        var (_, otherSlug) = await CreateTenantAsync(client, stranger, ct);
        (await ViewsAsync(client, otherSlug, stranger, ct)).Should().BeEmpty();

        // Deleting it takes it off everyone's default.
        var list = await (await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/views?table={Table}", slug, colleague, null), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);
        list.GetProperty("defaultView").GetString().Should().Be(id.ToString());
        (await client.SendAsync(Req(HttpMethod.Delete, $"/api/admin/views/{id}", slug, owner, null), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        list = await (await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/views?table={Table}", slug, colleague, null), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);
        list.GetProperty("views").GetArrayLength().Should().Be(0);
        list.GetProperty("defaultView").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static async Task<List<JsonElement>> ViewsAsync(HttpClient client, string slug, Guid user, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/views?table={Table}", slug, user, null), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(ct));
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("views").EnumerateArray().ToList();
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, string slug, Guid user, object? body)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", user.ToString());
        req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<(Guid Id, string Slug)> CreateTenantAsync(HttpClient client, Guid owner, CancellationToken ct)
    {
        var slug = "tv-" + Guid.NewGuid().ToString("N")[..8];
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{slug}@dcms.test" }),
        };
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return ((await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid(), slug);
    }

    /// <summary>A member holding one seeded role, in SQL: membership only comes from an invitation otherwise.</summary>
    private async Task<Guid> SeedMemberAsync(Guid tenantId, string roleName, CancellationToken ct)
    {
        var userId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO tenancy.tenant_memberships ("Id", "TenantId", "UserId", "Email", "CreatedAt")
            VALUES (@id, @tenant, @user, @email, now());
            INSERT INTO tenancy.member_roles ("Id", "TenantId", "MembershipId", "TenantRoleId")
            SELECT gen_random_uuid(), @tenant, @id, r."Id" FROM tenancy.tenant_roles r
            WHERE r."TenantId" = @tenant AND r."Name" = @role;
            """;
        insert.Parameters.AddWithValue("id", membershipId);
        insert.Parameters.AddWithValue("tenant", tenantId);
        insert.Parameters.AddWithValue("user", userId);
        insert.Parameters.AddWithValue("email", $"member-{userId:N}@dcms.test");
        insert.Parameters.AddWithValue("role", roleName);
        await insert.ExecuteNonQueryAsync(ct);
        return userId;
    }

    private async Task<Guid> RoleIdAsync(Guid tenantId, string roleName, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Id" FROM tenancy.tenant_roles WHERE "TenantId" = @tenant AND "Name" = @role""";
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("role", roleName);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
