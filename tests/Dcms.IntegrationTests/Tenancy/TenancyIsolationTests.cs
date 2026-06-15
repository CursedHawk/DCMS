using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dcms.IntegrationTests.Tenancy;

[Collection(AdminApiCollection.Name)]
public class TenancyIsolationTests(AdminApiFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    // The X-Dcms-Tenant header carries the tenant SLUG (Finbuckle resolves by identifier).
    private HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string roles = "", string? tenantSlug = null, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (!string.IsNullOrEmpty(roles))
        {
            req.Headers.Add("X-Test-Roles", roles);
        }
        if (tenantSlug is not null)
        {
            req.Headers.Add("X-Dcms-Tenant", tenantSlug);
        }
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        return req;
    }

    private async Task<Guid> CreateTenantAsync(HttpClient client, string slug, Guid owner, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "SuperAdmin",
            body: new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("tenantId").GetGuid();
    }

    private async Task<Guid> RoleIdAsync(HttpClient client, Guid actor, string tenantSlug, string roleName, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", actor, tenantSlug: tenantSlug), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var roles = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return roles.EnumerateArray().First(r => r.GetProperty("name").GetString() == roleName).GetProperty("id").GetGuid();
    }

    [DockerFact]
    public async Task Owner_reads_own_tenant_roles_but_is_forbidden_in_another_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var slugA = "iso-a-" + Guid.NewGuid().ToString("N")[..8];
        var slugB = "iso-b-" + Guid.NewGuid().ToString("N")[..8];

        await CreateTenantAsync(client, slugA, userA, ct);
        await CreateTenantAsync(client, slugB, userB, ct);

        var ownOk = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", userA, tenantSlug: slugA), ct);
        ownOk.StatusCode.Should().Be(HttpStatusCode.OK);

        var crossDenied = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", userA, tenantSlug: slugB), ct);
        crossDenied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [DockerFact]
    public async Task Me_tenants_lists_only_tenants_the_user_belongs_to()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var tenantA = await CreateTenantAsync(client, "mine-a-" + Guid.NewGuid().ToString("N")[..8], userA, ct);
        await CreateTenantAsync(client, "mine-b-" + Guid.NewGuid().ToString("N")[..8], userB, ct);

        var res = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/me/tenants", userA), ct);
        var tenants = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        var ids = tenants.EnumerateArray().Select(t => t.GetProperty("tenantId").GetGuid()).ToList();
        ids.Should().Contain(tenantA);
        ids.Should().HaveCount(1);
    }

    [DockerFact]
    public async Task Invitation_accept_grants_access_and_role_assignment_invalidates_permission_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var owner = Guid.NewGuid();
        var invitee = Guid.NewGuid();
        var slug = "inv-" + Guid.NewGuid().ToString("N")[..8];

        await CreateTenantAsync(client, slug, owner, ct);
        var memberRoleId = await RoleIdAsync(client, owner, slug, "Member", ct);
        var ownerRoleId = await RoleIdAsync(client, owner, slug, "Owner", ct);

        // Owner invites the invitee with the (low-privilege) Member role.
        var inviteRes = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/invitations", owner, tenantSlug: slug,
            body: new { email = $"{invitee:N}@dcms.test", roleIds = new[] { memberRoleId.ToString() } }), ct);
        inviteRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await inviteRes.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("token").GetString();

        // Invitee accepts → becomes a Member.
        var acceptRes = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/invitations/accept", invitee,
            body: new { token }), ct);
        acceptRes.StatusCode.Should().Be(HttpStatusCode.OK);

        // Member lacks RolesManage → forbidden. This also warms the perm cache.
        var beforeUpgrade = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", invitee, tenantSlug: slug), ct);
        beforeUpgrade.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Find the invitee's membership and grant the Owner role.
        var membersRes = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/members", owner, tenantSlug: slug), ct);
        var members = await membersRes.Content.ReadFromJsonAsync<JsonElement>(ct);
        var membershipId = members.EnumerateArray()
            .First(m => m.GetProperty("userId").GetGuid() == invitee)
            .GetProperty("membershipId").GetGuid();

        var assignRes = await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/members/{membershipId}/roles",
            owner, tenantSlug: slug, body: new { roleId = ownerRoleId.ToString() }), ct);
        assignRes.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Cache was invalidated on assignment → now allowed.
        var afterUpgrade = await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", invitee, tenantSlug: slug), ct);
        afterUpgrade.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
