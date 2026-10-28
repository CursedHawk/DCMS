using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;
using Npgsql;

namespace Dcms.IntegrationTests.Environments;

/// <summary>
/// A workspace's environments (ADR 0028): rows of one tenant, production's id the tenant's,
/// managed with the permission held in production, and deleted with the workspace.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class EnvironmentLifecycleTests(AdminApiFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string? target = null, object? body = null, string roles = "")
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (roles.Length > 0) req.Headers.Add("X-Test-Roles", roles);
        if (target is not null) EnvironmentTarget.Apply(req, target);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage res, CancellationToken ct) =>
        await res.Content.ReadFromJsonAsync<JsonElement>(ct);

    private static async Task<(string Slug, Guid TenantId, Guid Owner)> WorkspaceAsync(HttpClient client, CancellationToken ct)
    {
        var slug = "env-" + Guid.NewGuid().ToString("N")[..8];
        var owner = Guid.NewGuid();
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, roles: "SuperAdmin",
            body: new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (slug, (await JsonAsync(res, ct)).GetProperty("tenantId").GetGuid(), owner);
    }

    private static async Task<Guid> AddAsync(HttpClient client, string target, Guid owner, string name, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, target, new { name, empty = true }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await JsonAsync(res, ct)).GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, string slug, Guid user, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/environments", user, slug), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await JsonAsync(res, ct)).GetProperty("environments").EnumerateArray().ToList();
    }

    [DockerFact]
    public async Task Adding_environments_upstream_builds_the_pipeline_inside_the_one_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (slug, tenantId, owner) = await WorkspaceAsync(client, ct);

        var staging = await AddAsync(client, slug, owner, "staging", ct);
        var dev = await AddAsync(client, slug, owner, "development", ct);

        var list = await ListAsync(client, slug, owner, ct);
        list.Select(e => e.GetProperty("name").GetString())
            .Should().Equal("development", "staging", "production");
        var production = list[2];
        production.GetProperty("isProduction").GetBoolean().Should().BeTrue();
        production.GetProperty("id").GetGuid().Should().Be(tenantId, "production's id is the tenant's");
        production.GetProperty("promotesFrom").EnumerateArray().Select(x => x.GetGuid()).Should().Equal(staging);
        list[1].GetProperty("promotesFrom").EnumerateArray().Select(x => x.GetGuid()).Should().Equal(dev);

        // The same tenant, another environment: named by the header, with its own roles.
        var roles = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", owner, $"{slug}/development"), ct);
        roles.StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", owner, $"{slug}/nowhere"), ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The switcher lists workspaces, not environments.
        var mine = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/me/tenants", owner), ct), ct);
        mine.EnumerateArray().Select(t => t.GetProperty("slug").GetString()).Should().Equal(slug);

        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, slug, new { name = "staging" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "names are unique within a workspace");
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, slug, new { name = "Not OK" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.SendAsync(Req(HttpMethod.Patch, $"/api/admin/environments/{tenantId}", owner, slug, new { name = "live" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "production keeps its name");
    }

    [DockerFact]
    public async Task Environments_are_managed_with_the_production_permission_not_the_environment_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (slug, _, owner) = await WorkspaceAsync(client, ct);
        await AddAsync(client, slug, owner, "development", ct);

        // A stranger is not a member of the workspace at all.
        var stranger = Guid.NewGuid();
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", stranger, slug, new { name = "test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // From development, the owner still manages the workspace's environments, because they
        // hold environments:manage in production.
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, $"{slug}/development", new { name = "test", empty = true }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [DockerFact]
    public async Task Deleting_an_environment_unlinks_it_and_production_cannot_be_deleted_as_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (slug, tenantId, owner) = await WorkspaceAsync(client, ct);
        var dev = await AddAsync(client, slug, owner, "development", ct);
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/roles", owner, $"{slug}/development",
            new { name = "Developers", permissions = new[] { "content:read" } }), ct)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await client.SendAsync(Req(HttpMethod.Delete, $"/api/admin/environments/{tenantId}", owner, slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await client.SendAsync(Req(HttpMethod.Delete, $"/api/admin/environments/{dev}", owner, slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await ListAsync(client, slug, owner, ct);
        list.Should().ContainSingle();
        list[0].GetProperty("promotesFrom").EnumerateArray().Should().BeEmpty();
        var after = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/environments", owner, slug), ct), ct);
        after.GetProperty("enabled").GetBoolean().Should().BeFalse();
        (await CountAsync("tenancy.tenant_roles", "\"EnvironmentId\"", dev, ct)).Should().Be(0, "its roles went with it");
        (await CountAsync("tenancy.tenant_roles", "\"TenantId\"", tenantId, ct)).Should().BeGreaterThan(0, "production's stay");
    }

    [DockerFact]
    public async Task Deleting_the_workspace_deletes_every_environment()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (slug, tenantId, owner) = await WorkspaceAsync(client, ct);
        await AddAsync(client, slug, owner, "development", ct);
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/roles", owner, $"{slug}/development",
            new { name = "Developers", permissions = new[] { "content:read" } }), ct)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await client.SendAsync(Req(HttpMethod.Delete, "/api/admin/tenant", owner, slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var mine = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/me/tenants", owner), ct), ct);
        mine.EnumerateArray().Should().BeEmpty();
        (await CountAsync("tenancy.tenant_roles", "\"TenantId\"", tenantId, ct)).Should().Be(0, "development's rows went too");
        (await CountAsync("tenancy.environments", "\"TenantId\"", tenantId, ct)).Should().Be(0);
    }

    /// <summary>Counted as the database owner: what is left, whatever any filter or policy would show.</summary>
    private async Task<long> CountAsync(string table, string column, Guid value, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table} WHERE {column} = @v";
        cmd.Parameters.AddWithValue("v", value);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
