using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Npgsql;

namespace Dcms.IntegrationTests.Security;

/// <summary>
/// Desktop agents' tokens (ADR 0032, C2) against the real admin-api: a person mints one for one
/// workspace and some environments, optionally reads only; the agent then reaches only routes
/// offered to agents, only its workspace and environments, only the AI plane of the contracts,
/// and nothing once revoked or expired. It never mints or revokes tokens itself.
/// </summary>
[Collection(AdminApiCollection.Name)]
public sealed class AgentTokenTests(AdminApiFixture fixture)
{
    [DockerFact]
    public async Task An_agent_token_reaches_only_what_it_was_minted_for()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var owner = await SeedMemberAsync(tenantId, "Owner", ct);

        var minted = await Json(await client.SendAsync(AsPerson(HttpMethod.Post, "/api/admin/me/agent-tokens", slug, owner,
            new { name = "Claude Code on laptop", environments = new[] { "production" }, readOnly = false }), ct), ct);
        var token = minted.GetProperty("token").GetString()!;
        token.Should().StartWith("dcms_agt_");
        var id = minted.GetProperty("id").GetGuid();

        // Who am I: the person, as the agent, and what the token reaches.
        var me = await Json(await client.SendAsync(AsAgent(HttpMethod.Get, "/api/admin/me", slug, token), ct), ct);
        me.GetProperty("sub").GetString().Should().Be(owner.ToString());
        me.GetProperty("agent").GetProperty("name").GetString().Should().Be("Claude Code on laptop");
        me.GetProperty("roles").GetArrayLength().Should().Be(0, "an agent is never a SuperAdmin, nor anything by role claim");

        // A route nobody has offered to agents is closed to them (members stay a person's).
        await Refused(client.SendAsync(AsAgent(HttpMethod.Get, "/api/admin/members", slug, token), ct), "not offered");
        // One offered to them is open, as far as the person's own permissions reach.
        (await client.SendAsync(AsAgent(HttpMethod.Get, "/api/admin/sites", slug, token), ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        // The catalog: what the agent may call, never what is a person's alone.
        var catalog = await Json(await client.SendAsync(AsAgent(HttpMethod.Get, "/api/admin/cli/catalog", slug, token), ct), ct);
        var names = catalog.GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("name").GetString()).ToList();
        names.Should().Contain(["sites.list", "sites.commit", "media.upload", "contracts.invoke"]).And.NotContain(["agents.mint", "plugins.install"]);
        catalog.GetProperty("plane").GetString().Should().Be("ai");
        var person = await Json(await client.SendAsync(AsPerson(HttpMethod.Get, "/api/admin/cli/catalog", slug, owner), ct), ct);
        person.GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("name").GetString()).Should().Contain("agents.mint");
        var commit = catalog.GetProperty("operations").EnumerateArray().Single(o => o.GetProperty("name").GetString() == "sites.commit");
        commit.GetProperty("method").GetString().Should().Be("POST");
        commit.GetProperty("path").GetString().Should().Be("/api/admin/sites/{id:guid}/git/commit");
        commit.GetProperty("body").ValueKind.Should().Be(JsonValueKind.Object, "the body's JSON Schema, for the agent to fill");

        // The contracts: the AI plane only, as the console's own assistant.
        (await client.SendAsync(AsAgent(HttpMethod.Get, "/api/admin/contracts?plane=ai", slug, token), ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        await Refused(client.SendAsync(AsAgent(HttpMethod.Get, "/api/admin/contracts?plane=admin", slug, token), ct), "plane=ai");

        // Another environment than the one it was minted for.
        (await client.SendAsync(AsPerson(HttpMethod.Post, "/api/admin/environments", slug, owner, new { name = "staging", empty = true }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var staging = AsAgent(HttpMethod.Get, "/api/admin/me", slug, token);
        staging.Headers.Add("X-Dcms-Environment", "staging");
        await Refused(client.SendAsync(staging, ct), "staging");

        // Managing environments reaches beyond the one named: not for a token limited to some.
        await Refused(client.SendAsync(AsAgent(HttpMethod.Delete, $"/api/admin/environments/{Guid.NewGuid()}", slug, token), ct), "beyond one environment");

        // A token never mints or revokes tokens.
        await Refused(client.SendAsync(AsAgent(HttpMethod.Post, "/api/admin/me/agent-tokens", slug, token,
            new { name = "another", readOnly = false }), ct), "not offered");

        // The person's list shows it; revoking it ends it.
        var list = await Json(await client.SendAsync(AsPerson(HttpMethod.Get, "/api/admin/me/agent-tokens", slug, owner), ct), ct);
        list.EnumerateArray().Should().ContainSingle(t => t.GetProperty("id").GetGuid() == id);
        (await client.SendAsync(AsPerson(HttpMethod.Delete, $"/api/admin/me/agent-tokens/{id}", slug, owner), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.SendAsync(AsAgent(HttpMethod.Get, "/api/admin/me", slug, token), ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task A_read_only_token_reads_and_an_expired_one_does_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (tenantId, slug) = await CreateTenantAsync(client, ct);
        var owner = await SeedMemberAsync(tenantId, "Owner", ct);

        var reader = (await Json(await client.SendAsync(AsPerson(HttpMethod.Post, "/api/admin/me/agent-tokens", slug, owner,
            new { name = "reader", readOnly = true }), ct), ct)).GetProperty("token").GetString()!;
        var risks = (await Json(await client.SendAsync(AsAgent(HttpMethod.Get, "/api/admin/cli/catalog", slug, reader), ct), ct))
            .GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("risk").GetString()).Distinct().ToList();
        risks.Should().OnlyContain(r => r == "Read" || r == "PerOperation", "a read-only agent is shown only what it may call");
        await Refused(client.SendAsync(AsAgent(HttpMethod.Post, "/api/admin/sites", slug, reader, new { name = "x" }), ct), "only read");
        (await client.SendAsync(AsAgent(HttpMethod.Get, "/api/admin/contracts?plane=ai", slug, reader), ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        // dcms.plugins SetEnabled is Dangerous: refused before it is dispatched, whatever its input.
        await Refused(client.SendAsync(AsAgent(HttpMethod.Post, "/api/admin/contracts/dcms.plugins@1/SetEnabled?plane=ai", slug, reader,
            new { slug = "anything", enabled = false }), ct), "only read");

        // An environment the person holds no role in cannot be minted for.
        (await client.SendAsync(AsPerson(HttpMethod.Post, "/api/admin/me/agent-tokens", slug, owner,
            new { name = "nowhere", environments = new[] { "nowhere" }, readOnly = false }), ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var expiring = await Json(await client.SendAsync(AsPerson(HttpMethod.Post, "/api/admin/me/agent-tokens", slug, owner,
            new { name = "expiring", readOnly = false }), ct), ct);
        await using (var conn = new NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var past = conn.CreateCommand();
            past.CommandText = """UPDATE tenancy.agent_tokens SET "ExpiresAt" = now() - interval '1 minute' WHERE "Id" = @id""";
            past.Parameters.AddWithValue("id", expiring.GetProperty("id").GetGuid());
            await past.ExecuteNonQueryAsync(ct);
        }
        (await client.SendAsync(AsAgent(HttpMethod.Get, "/api/admin/me", slug, expiring.GetProperty("token").GetString()!), ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task Refused(Task<HttpResponseMessage> call, string because)
    {
        var res = await call;
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, text);
        text.Should().Contain("agent_scope").And.Contain(because);
    }

    private static HttpRequestMessage AsAgent(HttpMethod method, string url, string slug, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static HttpRequestMessage AsPerson(HttpMethod method, string url, string slug, Guid user, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", user.ToString());
        req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        res.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<(Guid TenantId, string Slug)> CreateTenantAsync(HttpClient client, CancellationToken ct)
    {
        var slug = "agt-" + Guid.NewGuid().ToString("N")[..8];
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants")
        {
            Headers = { { "X-Test-Sub", Guid.NewGuid().ToString() }, { "X-Test-Roles", "SuperAdmin" } },
            Content = JsonContent.Create(new { slug, name = slug, ownerUserId = Guid.NewGuid(), ownerEmail = $"{slug}@dcms.test" }),
        };
        var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return ((await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid(), slug);
    }

    /// <summary>A member holding one seeded role, in SQL: membership only comes from an invitation otherwise.</summary>
    private async Task<Guid> SeedMemberAsync(Guid tenantId, string roleName, CancellationToken ct)
    {
        var userId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO tenancy.tenant_memberships ("Id", "TenantId", "UserId", "Email", "CreatedAt")
            VALUES (@id, @tenant, @user, @email, now());
            INSERT INTO tenancy.member_roles ("Id", "TenantId", "EnvironmentId", "MembershipId", "TenantRoleId")
            SELECT gen_random_uuid(), @tenant, r."EnvironmentId", @id, r."Id" FROM tenancy.tenant_roles r
            WHERE r."TenantId" = @tenant AND r."Name" = @role;
            """;
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("tenant", tenantId);
        insert.Parameters.AddWithValue("user", userId);
        insert.Parameters.AddWithValue("email", $"member-{userId:N}@dcms.test");
        insert.Parameters.AddWithValue("role", roleName);
        await insert.ExecuteNonQueryAsync(ct);
        return userId;
    }
}
