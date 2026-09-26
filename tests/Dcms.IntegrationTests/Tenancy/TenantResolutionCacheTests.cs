using System.Net;
using System.Net.Http.Json;

namespace Dcms.IntegrationTests.Tenancy;

/// <summary>
/// TenantStore caches resolved tenants for 30 s. These pin the two ways that cache could turn
/// into a correctness bug: a suspension that no longer bites until the entry expires, and a
/// new tenant that is "not found" because its absence was remembered.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class TenantResolutionCacheTests(AdminApiFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string? roles = null, string? tenantSlug = null, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (roles is not null) req.Headers.Add("X-Test-Roles", roles);
        if (tenantSlug is not null) req.Headers.Add("X-Dcms-Tenant", tenantSlug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<Guid> CreateTenantAsync(HttpClient client, string slug, Guid owner, CancellationToken ct)
    {
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "SuperAdmin",
            body: new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct)).GetProperty("tenantId").GetGuid();
    }

    [DockerFact]
    public async Task Suspension_applies_to_the_next_request_even_with_the_tenant_cached()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var owner = Guid.NewGuid();
        var slug = "cache-s-" + Guid.NewGuid().ToString("N")[..8];
        var tenantId = await CreateTenantAsync(client, slug, owner, ct);

        // Resolves the tenant, and so caches it.
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", owner, tenantSlug: slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/tenants/{tenantId}/suspend", SuperAdmin, "SuperAdmin"), ct))
            .EnsureSuccessStatusCode();

        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", owner, tenantSlug: slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "the suspend endpoint evicts the cached tenant");
    }

    [DockerFact]
    public async Task A_slug_deleted_and_recreated_resolves_to_the_new_tenant()
    {
        // What the load-test teardown + reseed does: without the evictions the second tenant
        // resolved to the purged first one for 30 s, and its owner was refused.
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var slug = "cache-d-" + Guid.NewGuid().ToString("N")[..8];

        await CreateTenantAsync(client, slug, first, ct);
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", first, tenantSlug: slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Req(HttpMethod.Delete, "/api/admin/tenant", first, tenantSlug: slug), ct))
            .EnsureSuccessStatusCode();

        await CreateTenantAsync(client, slug, second, ct);
        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", second, tenantSlug: slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the purge and the create both evict the slug");
    }

    [DockerFact]
    public async Task A_tenant_asked_for_before_it_existed_resolves_once_created()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var owner = Guid.NewGuid();
        var slug = "cache-n-" + Guid.NewGuid().ToString("N")[..8];

        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", owner, tenantSlug: slug), ct))
            .StatusCode.Should().NotBe(HttpStatusCode.OK);

        await CreateTenantAsync(client, slug, owner, ct);

        (await client.SendAsync(Req(HttpMethod.Get, "/api/admin/roles", owner, tenantSlug: slug), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK, "a miss is never cached");
    }
}
