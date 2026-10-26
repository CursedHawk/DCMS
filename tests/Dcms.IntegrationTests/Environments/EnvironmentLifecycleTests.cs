using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;

namespace Dcms.IntegrationTests.Environments;

/// <summary>
/// A workspace's environments (ADR 0026): each one a whole tenant, grouped by
/// <c>WorkspaceId</c>, managed from the root, deleted and suspended with it.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class EnvironmentLifecycleTests(AdminApiFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string? tenantSlug = null, object? body = null, string roles = "")
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (roles.Length > 0) req.Headers.Add("X-Test-Roles", roles);
        if (tenantSlug is not null) req.Headers.Add("X-Dcms-Tenant", tenantSlug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage res, CancellationToken ct) =>
        await res.Content.ReadFromJsonAsync<JsonElement>(ct);

    private static async Task<(string Slug, Guid Owner)> WorkspaceAsync(HttpClient client, CancellationToken ct)
    {
        var slug = "env-" + Guid.NewGuid().ToString("N")[..8];
        var owner = Guid.NewGuid();
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, roles: "SuperAdmin",
            body: new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments/enable", owner, slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        return (slug, owner);
    }

    private static async Task<Guid> AddAsync(HttpClient client, string slug, Guid owner, string name, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, slug, new { name }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await JsonAsync(res, ct)).GetProperty("tenantId").GetGuid();
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, string slug, Guid user, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/environments", user, slug), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await JsonAsync(res, ct)).GetProperty("environments").EnumerateArray().ToList();
    }

    [DockerFact]
    public async Task Adding_environments_upstream_builds_the_pipeline_and_each_is_its_own_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (slug, owner) = await WorkspaceAsync(client, ct);

        var staging = await AddAsync(client, slug, owner, "staging", ct);
        var dev = await AddAsync(client, slug, owner, "development", ct);

        var list = await ListAsync(client, slug, owner, ct);
        list.Select(e => e.GetProperty("name").GetString())
            .Should().Equal("development", "staging", "production");
        var production = list[2];
        production.GetProperty("isRoot").GetBoolean().Should().BeTrue();
        production.GetProperty("promotesFrom").EnumerateArray().Select(x => x.GetGuid()).Should().Equal(staging);
        list[1].GetProperty("promotesFrom").EnumerateArray().Select(x => x.GetGuid()).Should().Equal(dev);
        list[0].GetProperty("slug").GetString().Should().Be($"{slug}-development");

        // A separate tenant: the creator works in it under its own slug, with its own roles.
        var roles = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", owner, $"{slug}-development"), ct);
        roles.StatusCode.Should().Be(HttpStatusCode.OK);

        var mine = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/me/tenants", owner), ct), ct);
        mine.EnumerateArray().Select(t => t.GetProperty("environment").GetString())
            .Should().BeEquivalentTo(["production", "staging", "development"]);
        mine.EnumerateArray().Select(t => t.GetProperty("workspaceId").GetGuid()).Distinct().Should().ContainSingle();

        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, slug, new { name = "staging" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "names are unique within a workspace");
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, slug, new { name = "Not OK" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task Environments_are_managed_with_the_root_permission_not_the_environment_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (slug, owner) = await WorkspaceAsync(client, ct);
        await AddAsync(client, slug, owner, "development", ct);

        // A stranger is not a member of the workspace at all.
        var stranger = Guid.NewGuid();
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", stranger, slug, new { name = "test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The development environment's creator owns it, and still manages the workspace's
        // environments only because they hold environments:manage in the root.
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, $"{slug}-development", new { name = "test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [DockerFact]
    public async Task Deleting_an_environment_unlinks_it_and_the_root_cannot_be_deleted_as_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (slug, owner) = await WorkspaceAsync(client, ct);
        var dev = await AddAsync(client, slug, owner, "development", ct);
        var root = (await ListAsync(client, slug, owner, ct)).Single(e => e.GetProperty("isRoot").GetBoolean())
            .GetProperty("tenantId").GetGuid();

        (await client.SendAsync(Req(HttpMethod.Delete, $"/api/admin/environments/{root}", owner, slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments/disable", owner, slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "an environment would be orphaned");

        (await client.SendAsync(Req(HttpMethod.Delete, $"/api/admin/environments/{dev}", owner, slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await ListAsync(client, slug, owner, ct);
        list.Should().ContainSingle();
        list[0].GetProperty("promotesFrom").EnumerateArray().Should().BeEmpty();

        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments/disable", owner, slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/environments", owner, slug), ct), ct);
        after.GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    [DockerFact]
    public async Task Deleting_the_workspace_deletes_its_environments()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (slug, owner) = await WorkspaceAsync(client, ct);
        await AddAsync(client, slug, owner, "development", ct);

        (await client.SendAsync(Req(HttpMethod.Delete, "/api/admin/tenant", owner, slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var mine = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/me/tenants", owner), ct), ct);
        mine.EnumerateArray().Should().BeEmpty();
    }
}
