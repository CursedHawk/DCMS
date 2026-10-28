using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;

namespace Dcms.IntegrationTests.Environments;

/// <summary>
/// One member list per workspace, one set of roles per environment (ADR 0028): members are added
/// and removed in production, belong to every environment, and are let into one only by a role there.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class EnvironmentMembersTests(AdminApiFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string? tenantSlug = null, object? body = null, string roles = "")
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (roles.Length > 0) req.Headers.Add("X-Test-Roles", roles);
        if (tenantSlug is not null) EnvironmentTarget.Apply(req, tenantSlug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage res, CancellationToken ct) =>
        await res.Content.ReadFromJsonAsync<JsonElement>(ct);

    private static async Task<(string Slug, Guid Owner)> WorkspaceAsync(HttpClient client, CancellationToken ct)
    {
        var slug = "envm-" + Guid.NewGuid().ToString("N")[..8];
        var owner = Guid.NewGuid();
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, roles: "SuperAdmin",
            body: new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        return (slug, owner);
    }

    private static async Task<Guid> RoleIdAsync(HttpClient client, Guid actor, string slug, string name, CancellationToken ct)
    {
        var roles = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", actor, slug), ct), ct);
        return roles.EnumerateArray().First(r => r.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> JoinAsync(HttpClient client, string slug, Guid owner, Guid roleId, CancellationToken ct)
    {
        var member = Guid.NewGuid();
        var invite = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/invitations", owner, slug,
            new { email = $"{member:N}@dcms.test", roleIds = new[] { roleId.ToString() } }), ct);
        invite.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await JsonAsync(invite, ct)).GetProperty("token").GetString();
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/invitations/accept", member, body: new { token }), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return member;
    }

    private static async Task<Guid> MembershipIdAsync(HttpClient client, Guid owner, string slug, Guid user, CancellationToken ct)
    {
        var members = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/members", owner, slug), ct), ct);
        return members.EnumerateArray().First(m => m.GetProperty("userId").GetGuid() == user).GetProperty("membershipId").GetGuid();
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        for (var i = 0; i < 100 && !await condition(); i++)
        {
            await Task.Delay(100, ct);
        }
        (await condition()).Should().BeTrue();
    }

    [DockerFact]
    public async Task A_new_environment_carries_the_workspace_members_with_their_roles_and_a_role_is_the_way_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (slug, owner) = await WorkspaceAsync(client, ct);
        var member = await JoinAsync(client, slug, owner, await RoleIdAsync(client, owner, slug, "Member", ct), ct);

        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, slug, new { name = "development" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var dev = $"{slug}/development";

        // Seeded with the Member role they hold in production: they can work there.
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/me/permissions", member, dev), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/environments", member, dev), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Without a role in development they still belong to the workspace, and get nothing from it.
        var membership = await MembershipIdAsync(client, owner, dev, member, ct);
        var memberRole = await RoleIdAsync(client, owner, dev, "Member", ct);
        (await client.SendAsync(Req(HttpMethod.Delete, $"/api/admin/members/{membership}/roles/{memberRole}", owner, dev), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", member, dev), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // The list stays theirs to read: it is how they find an environment they can open.
        var listed = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/environments", member, dev), ct), ct);
        listed.GetProperty("environments").EnumerateArray()
            .Select(e => (e.GetProperty("name").GetString(), e.GetProperty("isMember").GetBoolean()))
            .Should().BeEquivalentTo([("development", false), ("production", true)]);
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/environments", Guid.NewGuid(), dev), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "a stranger is no member of the workspace");
        var mine = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/me/tenants", member), ct), ct);
        mine.EnumerateArray().Select(t => t.GetProperty("slug").GetString()).Should().Equal(slug);

        // Members are added and removed in the root only.
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/invitations", owner, dev,
            new { email = "someone@dcms.test", roleIds = new[] { memberRole.ToString() } }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.SendAsync(Req(HttpMethod.Delete, $"/api/admin/members/{membership}", owner, dev), ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task Joining_and_leaving_the_workspace_reaches_every_environment()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (slug, owner) = await WorkspaceAsync(client, ct);
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, slug, new { name = "development", empty = true }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var dev = $"{slug}/development";

        // One member list: someone who joined in production is a member in development at once,
        // holding no role there until given one.
        var member = await JoinAsync(client, slug, owner, await RoleIdAsync(client, owner, slug, "Member", ct), ct);
        async Task<bool> InDev()
        {
            var members = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/members", owner, dev), ct), ct);
            return members.EnumerateArray().Any(m => m.GetProperty("userId").GetGuid() == member);
        }
        (await InDev()).Should().BeTrue();
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", member, dev), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "no role in development yet");

        var membership = await MembershipIdAsync(client, owner, slug, member, ct);
        (await client.SendAsync(Req(HttpMethod.Delete, $"/api/admin/members/{membership}", owner, slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await InDev()).Should().BeFalse();
    }

    [DockerFact]
    public async Task Owner_in_production_is_owner_in_every_environment()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (slug, owner) = await WorkspaceAsync(client, ct);
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, slug, new { name = "development", empty = true }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var dev = $"{slug}/development";
        var member = await JoinAsync(client, slug, owner, await RoleIdAsync(client, owner, slug, "Member", ct), ct);

        // Owner is granted in production only, and follows into development.
        var devOwner = await RoleIdAsync(client, owner, dev, "Owner", ct);
        (await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/members/{await MembershipIdAsync(client, owner, dev, member, ct)}/roles", owner, dev,
            new { roleId = devOwner }), ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/members/{await MembershipIdAsync(client, owner, slug, member, ct)}/roles", owner, slug,
            new { roleId = await RoleIdAsync(client, owner, slug, "Owner", ct) }), ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        async Task<bool> OwnsDev() =>
            (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", member, dev), ct)).StatusCode == HttpStatusCode.OK;
        await EventuallyAsync(OwnsDev, ct);
    }

    [DockerFact]
    public async Task A_branch_takes_its_people_from_where_it_came_from_and_needs_a_way_in_there()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (slug, owner) = await WorkspaceAsync(client, ct);
        var role = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/roles", owner, slug,
            new { name = "Env admin", permissions = new[] { "environments:manage" } }), ct);
        var manager = await JoinAsync(client, slug, owner, (await JsonAsync(role, ct)).GetProperty("id").GetGuid(), ct);
        var staging = await JsonAsync(await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, slug,
            new { name = "staging", empty = true }), ct), ct);
        var stagingId = staging.GetProperty("id").GetGuid();

        // Staging shuts the manager out; branching it would hand them a copy of what they cannot open.
        var membership = await MembershipIdAsync(client, owner, $"{slug}/staging", manager, ct);
        var envAdmin = await RoleIdAsync(client, owner, $"{slug}/staging", "Env admin", ct);
        (await client.SendAsync(Req(HttpMethod.Delete, $"/api/admin/members/{membership}/roles/{envAdmin}", owner, $"{slug}/staging"), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", manager, slug,
            new { name = "staging-hotfix", from = stagingId, feature = true }), ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // Nor a plain duplicate of it, with or without its data.
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", manager, slug,
            new { name = "staging-copy", from = stagingId, copy = new[] { "dynamic-apps-records" } }), ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The owner may; the branch's people hold what they hold in staging, so the manager has no way in.
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, slug,
            new { name = "staging-hotfix", from = stagingId, feature = true }), ct)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", manager, $"{slug}/staging-hotfix"), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [DockerFact]
    public async Task A_delegated_environment_manager_does_not_become_owner_of_what_they_create()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var (slug, owner) = await WorkspaceAsync(client, ct);

        var role = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/roles", owner, slug,
            new { name = "Env admin", permissions = new[] { "environments:manage" } }), ct);
        role.StatusCode.Should().Be(HttpStatusCode.Created);
        var manager = await JoinAsync(client, slug, owner, (await JsonAsync(role, ct)).GetProperty("id").GetGuid(), ct);

        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", manager, slug, new { name = "test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // They hold in "test" what they hold in the root -- not Owner, so no roles:manage.
        var permissions = await JsonAsync(await client.SendAsync(Req(HttpMethod.Get, "/api/admin/me/permissions", manager, $"{slug}/test"), ct), ct);
        permissions.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().Equal("environments:manage");
        // The workspace owner owns it.
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", owner, $"{slug}/test"), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
