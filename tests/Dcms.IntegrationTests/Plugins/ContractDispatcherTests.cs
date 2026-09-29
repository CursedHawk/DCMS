using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// The contract dispatcher over HTTP (plan M5): the site plane reaches only Site-exposed
/// operations of an enabled provider, input is bound strictly, and the admin plane checks each
/// operation's permission.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class ContractDispatcherTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task Site_plane_serves_site_operations_of_an_enabled_provider_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, owner, instanceId) = await SiteAsync(ct);
        var content = fixture.Content.CreateClient();

        var catalog = await (await content.SendAsync(Req(HttpMethod.Get, tenant, "/api/_contracts"), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);
        var ops = catalog.EnumerateArray()
            .SelectMany(c => c.GetProperty("operations").EnumerateArray().Select(o => $"{c.GetProperty("id").GetString()} {o.GetProperty("name").GetString()}"))
            .ToList();
        ops.Should().Contain(["visitors.identity@1 GetCurrent", "visitors.profiles@1 GetPublic"]);
        ops.Should().NotContain("visitors.profiles@1 Get", "an Admin-only operation is not listed to the site");

        var anonymous = await content.SendAsync(Req(HttpMethod.Post, tenant, "/api/members/_contracts/visitors.identity@1/GetCurrent"), ct);
        anonymous.StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("visitor").ValueKind.Should().Be(JsonValueKind.Null);

        var token = await RegisterAsync(content, tenant, "eve@site.test", ct);
        var signedIn = await content.SendAsync(Req(HttpMethod.Post, tenant, "/api/members/_contracts/visitors.identity@1/GetCurrent", token: token), ct);
        (await signedIn.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("visitor").GetProperty("email").GetString()
            .Should().Be("eve@site.test");

        var adminOnly = await content.SendAsync(Req(HttpMethod.Post, tenant, "/api/members/_contracts/visitors.profiles@1/Get",
            body: new { visitorId = Guid.NewGuid() }), ct);
        adminOnly.StatusCode.Should().Be(HttpStatusCode.NotFound, "not exposed on this plane, and not discoverable either");

        var unknownField = await content.SendAsync(Req(HttpMethod.Post, tenant, "/api/members/_contracts/visitors.profiles@1/GetPublic",
            body: new { visitorId = Guid.NewGuid(), extra = 1 }), ct);
        unknownField.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var missing = await content.SendAsync(Req(HttpMethod.Post, tenant, "/api/members/_contracts/visitors.profiles@1/GetPublic", body: new { }), ct);
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var wrongInstance = await content.SendAsync(Req(HttpMethod.Post, tenant, "/api/nosuch/_contracts/visitors.identity@1/GetCurrent"), ct);
        wrongInstance.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Disable the provider: the operations disappear from the site.
        var admin = fixture.Admin.CreateClient();
        (await admin.SendAsync(AdminReq(HttpMethod.Post, $"/api/admin/plugins/instances/{instanceId}/disable", owner, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await (await content.SendAsync(Req(HttpMethod.Get, tenant, "/api/_contracts"), ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        after.EnumerateArray().Select(c => c.GetProperty("id").GetString()).Should().NotContain("visitors.identity@1");
    }

    [DockerFact]
    public async Task Admin_plane_invokes_with_the_members_permissions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, owner, _) = await SiteAsync(ct);
        var content = fixture.Content.CreateClient();
        var token = await RegisterAsync(content, tenant, "fay@site.test", ct);
        var me = await (await content.SendAsync(Req(HttpMethod.Get, tenant, "/api/members/me", token: token), ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        var visitorId = me.GetProperty("id").GetGuid();

        var admin = fixture.Admin.CreateClient();

        // Plugin permissions are granted through roles; the Owner role is seeded with platform
        // permissions only, so the owner is refused until a role grants plugin:visitor-auth:read.
        var refused = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/contracts/visitors.profiles@1/Get?plane=admin", owner, tenant,
            body: new { visitorId }), ct);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var res = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/contracts/visitors.profiles@1/Get?plane=admin", SuperAdmin, tenant, "SuperAdmin",
            body: new { visitorId }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(ct));
        (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("email").GetString().Should().Be("fay@site.test");

        var opsOf = async (Guid sub, string roles) =>
            (await (await admin.SendAsync(AdminReq(HttpMethod.Get, "/api/admin/contracts?plane=admin", sub, tenant, roles), ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray()
            .SelectMany(c => c.GetProperty("operations").EnumerateArray().Select(o => $"{c.GetProperty("id").GetString()} {o.GetProperty("name").GetString()}"))
            .ToList();
        (await opsOf(SuperAdmin, "SuperAdmin")).Should().Contain("visitors.profiles@1 Get");
        (await opsOf(owner, "")).Should().NotContain("visitors.profiles@1 Get", "an operation the caller cannot use is left out, not listed disabled");

        var anonymous = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/contracts?plane=admin")
        {
            Headers = { { "X-Dcms-Tenant", tenant } },
        }, ct);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task Ai_plane_reaches_only_instances_the_tenant_opted_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, _, instanceId) = await SiteAsync(ct);
        var admin = fixture.Admin.CreateClient();

        async Task<List<string?>> AiContracts() =>
            (await (await admin.SendAsync(AdminReq(HttpMethod.Get, "/api/admin/contracts?plane=ai", SuperAdmin, tenant, "SuperAdmin"), ct))
                .Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();

        (await AiContracts()).Should().NotContain("visitors.profiles@1", "AI tools are off until the tenant opts the instance in");
        var refused = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/contracts/visitors.profiles@1/ListAttributeDefinitions?plane=ai",
            SuperAdmin, tenant, "SuperAdmin", new { }), ct);
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await admin.SendAsync(AdminReq(HttpMethod.Put, $"/api/admin/plugins/instances/{instanceId}", SuperAdmin, tenant, "SuperAdmin",
            new { aiToolsEnabled = true }), ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await AiContracts()).Should().Contain("visitors.profiles@1");
        var allowed = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/contracts/visitors.profiles@1/ListAttributeDefinitions?plane=ai",
            SuperAdmin, tenant, "SuperAdmin", new { }), ct);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync(ct));
    }

    [DockerFact]
    public async Task A_write_through_the_dispatcher_is_audited_as_a_contract_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, _, _) = await SiteAsync(ct);
        var content = fixture.Content.CreateClient();
        var token = await RegisterAsync(content, tenant, "gil@site.test", ct);
        var visitorId = (await (await content.SendAsync(Req(HttpMethod.Get, tenant, "/api/members/me", token: token), ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        var admin = fixture.Admin.CreateClient();
        var res = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/contracts/visitors.profiles@1/SetAttributes?plane=admin",
            SuperAdmin, tenant, "SuperAdmin", new { visitorId, attributes = new Dictionary<string, object>() }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(ct));

        // The route is AuditExempt (per-route records would say nothing); the proxy's record of
        // the Safe operation is what must reach the chain.
        var found = false;
        for (var i = 0; i < 40 && !found; i++)
        {
            using var scope = fixture.Admin.Services.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<Dcms.Shared.Data.Audit.AuditDbContext>();
            using var rls = Dcms.Shared.Data.Rls.RlsScope.Platform();
            found = await audit.Events.AsNoTracking().AnyAsync(e => e.Action == "plugin.contract.invoked", ct);
            if (!found) await Task.Delay(250, ct);
        }
        found.Should().BeTrue("a Safe contract operation is recorded as plugin.contract.invoked");
    }

    // ---- helpers ----

    private async Task<(string Slug, Guid Owner, Guid InstanceId)> SiteAsync(CancellationToken ct)
    {
        var admin = fixture.Admin.CreateClient();
        var owner = Guid.NewGuid();
        var slug = "dsp-" + Guid.NewGuid().ToString("N")[..8];
        (await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct)).StatusCode.Should().Be(HttpStatusCode.Created);
        var res = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/plugins/instances", owner, slug,
            body: new { pluginId = "visitor-auth", slug = "members", name = "Members", config = "{}" }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (slug, owner, (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid());
    }

    private static async Task<string> RegisterAsync(HttpClient content, string tenant, string email, CancellationToken ct)
    {
        var res = await content.SendAsync(Req(HttpMethod.Post, tenant, "/api/members/register",
            body: new { email, password = "correct horse battery staple" }), ct);
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString()!;
    }

    private static HttpRequestMessage Req(HttpMethod method, string tenant, string url, object? body = null, string? token = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Dcms-Tenant", tenant);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static HttpRequestMessage AdminReq(HttpMethod method, string url, Guid sub, string slug, string roles = "", object? body = null)
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
