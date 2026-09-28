using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.Plugins.VisitorAuth.Contracts;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.Visitors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dcms.IntegrationTests.Visitors;

/// <summary>
/// VisitorAuth now that it owns its routes (plan M3): the public wire format is unchanged, the
/// profile honours the tenant's attribute definitions, and what other plugins see through
/// <c>visitors.profiles@1</c> never includes a private attribute.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class VisitorProfileTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private const string Config = """
        {"attributes":[
          {"key":"company","label":"Company","visibility":"plugins"},
          {"key":"note","label":"Private note","visibility":"private"},
          {"key":"tier","label":"Tier","type":"select","options":["free","pro"],"visibility":"public","visitorEditable":false}
        ]}
        """;

    private sealed record Tokens(string AccessToken, string RefreshToken);

    [DockerFact]
    public async Task Visitor_registers_and_edits_own_profile_within_the_definitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, _) = await SiteWithVisitorAuthAsync(ct);
        var content = fixture.Content.CreateClient();

        var tokens = await RegisterAsync(content, tenant, "ada@site.test", ct);

        var me = await content.SendAsync(Req(HttpMethod.Get, tenant, "/api/members/me", tokens.AccessToken), ct);
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        (await me.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("email").GetString().Should().Be("ada@site.test");

        var update = await content.SendAsync(Req(HttpMethod.Put, tenant, "/api/members/me/profile", tokens.AccessToken,
            new { displayName = "Ada", attributes = new { company = "Analytical Engines", note = "likes tea" } }), ct);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await update.Content.ReadFromJsonAsync<JsonElement>(ct);
        profile.GetProperty("displayName").GetString().Should().Be("Ada");
        profile.GetProperty("attributes").GetProperty("note").GetString().Should().Be("likes tea", "the visitor sees their own private attributes");

        var undefined = await content.SendAsync(Req(HttpMethod.Put, tenant, "/api/members/me/profile", tokens.AccessToken,
            new { attributes = new { shoeSize = 42 } }), ct);
        undefined.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var notEditable = await content.SendAsync(Req(HttpMethod.Put, tenant, "/api/members/me/profile", tokens.AccessToken,
            new { attributes = new { tier = "pro" } }), ct);
        notEditable.StatusCode.Should().Be(HttpStatusCode.BadRequest, "tier is set by the site, not the visitor");

        var anonymous = await content.SendAsync(Req(HttpMethod.Get, tenant, "/api/members/me/profile", null), ct);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task A_token_from_one_tenant_is_refused_by_another()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenantA, _) = await SiteWithVisitorAuthAsync(ct);
        var (tenantB, _) = await SiteWithVisitorAuthAsync(ct);
        var content = fixture.Content.CreateClient();

        var tokens = await RegisterAsync(content, tenantA, "bob@site.test", ct);

        var res = await content.SendAsync(Req(HttpMethod.Get, tenantB, "/api/members/me", tokens.AccessToken), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task Other_plugins_never_see_a_private_attribute()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, tenantId) = await SiteWithVisitorAuthAsync(ct);
        var content = fixture.Content.CreateClient();
        var tokens = await RegisterAsync(content, tenant, "cy@site.test", ct);
        await content.SendAsync(Req(HttpMethod.Put, tenant, "/api/members/me/profile", tokens.AccessToken,
            new { attributes = new { company = "Acme", note = "secret" } }), ct);

        using var scope = fixture.Content.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VisitorsDbContext>();
        var context = new ProviderContext(tenantId, JsonDocument.Parse(Config));
        var profiles = new VisitorProfiles(context, db, NullLogger<VisitorProfiles>.Instance);

        var visitorId = await VisitorIdAsync(content, tenant, tokens.AccessToken, ct);
        var profile = await profiles.GetAsync(new VisitorRef(visitorId), ct);
        profile!.Attributes.Keys.Should().BeEquivalentTo(["company"]);

        var publicProfile = await profiles.GetPublicAsync(new VisitorRef(visitorId), ct);
        publicProfile!.Attributes.Should().BeEmpty("nothing public was set");

        var act = () => profiles.SetAttributesAsync(
            new SetVisitorAttributes(visitorId, new Dictionary<string, JsonElement> { ["note"] = JsonSerializer.SerializeToElement("overwritten") }), ct);
        await act.Should().ThrowAsync<ContractValidationException>().WithMessage("*'note' cannot be changed here*");
    }

    // ---- helpers ----

    private sealed record ProviderContext(Guid TenantId, JsonDocument Config) : IPluginContext
    {
        public string PluginId => "visitor-auth";
        public PluginInstanceContext? Instance => new(Guid.NewGuid(), TenantId, PluginId, "members", "Members", "", Config);
        public PluginActor Actor => PluginActor.System;
        public IPluginContracts Contracts => throw new NotSupportedException("no events in this test");
    }

    private async Task<(string Slug, Guid TenantId)> SiteWithVisitorAuthAsync(CancellationToken ct)
    {
        var admin = fixture.Admin.CreateClient();
        var owner = Guid.NewGuid();
        var slug = "vis-" + Guid.NewGuid().ToString("N")[..8];

        var tenant = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct);
        tenant.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenantId = (await tenant.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid();

        var instance = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/plugins/instances", owner, slug, body:
            new { pluginId = "visitor-auth", slug = "members", name = "Members", config = Config }), ct);
        instance.StatusCode.Should().Be(HttpStatusCode.Created, await instance.Content.ReadAsStringAsync(ct));
        return (slug, tenantId);
    }

    private static async Task<Tokens> RegisterAsync(HttpClient content, string tenant, string email, CancellationToken ct)
    {
        var res = await content.SendAsync(Req(HttpMethod.Post, tenant, "/api/members/register", null,
            new { email, password = "correct horse battery staple" }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(ct));
        return (await res.Content.ReadFromJsonAsync<Tokens>(ct))!;
    }

    private static async Task<Guid> VisitorIdAsync(HttpClient content, string tenant, string token, CancellationToken ct)
    {
        var me = await content.SendAsync(Req(HttpMethod.Get, tenant, "/api/members/me", token), ct);
        return (await me.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private static HttpRequestMessage Req(HttpMethod method, string tenant, string url, string? token, object? body = null)
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
