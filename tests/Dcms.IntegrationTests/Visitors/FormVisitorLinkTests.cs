using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Visitors;

/// <summary>
/// Forms consuming <c>visitors.identity@1</c> (plan M4a): a signed-in visitor's submission is
/// linked to them and prefilled from what VisitorAuth shares with plugins; an anonymous one is
/// stored exactly as before.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class FormVisitorLinkTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private const string VisitorConfig = """{"attributes":[{"key":"company","label":"Company","visibility":"plugins"},{"key":"secret","label":"Secret","visibility":"private"}]}""";

    private const string FormsConfig = """
        {"forms":[{"name":"contact","title":"Contact","fields":[
          {"name":"email","type":"email","required":true,"prefill":"visitor.email"},
          {"name":"company","type":"text","prefill":"visitor.company"},
          {"name":"secret","type":"text","prefill":"visitor.secret"},
          {"name":"message","type":"textarea","required":true}
        ]}]}
        """;

    [DockerFact]
    public async Task Signed_in_submission_is_linked_and_prefilled_from_shared_attributes_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, tenantId) = await SiteAsync(ct);
        var content = fixture.Content.CreateClient();

        var register = await content.SendAsync(Req(HttpMethod.Post, tenant, "/api/members/register", null,
            new { email = "dee@site.test", password = "correct horse battery staple" }), ct);
        var token = (await register.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString()!;
        await content.SendAsync(Req(HttpMethod.Put, tenant, "/api/members/me/profile", token,
            new { attributes = new { company = "Acme", secret = "do-not-share" } }), ct);

        var signedIn = await content.SendAsync(Req(HttpMethod.Post, tenant, "/api/contact/forms/contact", token,
            new { message = "Hello" }), ct);
        signedIn.StatusCode.Should().Be(HttpStatusCode.Accepted, await signedIn.Content.ReadAsStringAsync(ct));
        var linkedId = (await signedIn.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("submissionId").GetGuid();

        var anonymous = await content.SendAsync(Req(HttpMethod.Post, tenant, "/api/contact/forms/contact", null,
            new { message = "Hi" }), ct);
        anonymous.StatusCode.Should().Be(HttpStatusCode.BadRequest, "email is required and there is nobody to prefill it from");

        using var scope = fixture.Content.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FormsDbContext>();
        using var rls = RlsScope.Tenant(tenantId);
        var stored = await db.Submissions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == linkedId, ct);

        stored.VisitorId.Should().NotBeNull();
        var data = JsonDocument.Parse(stored.DataJson).RootElement;
        data.GetProperty("email").GetString().Should().Be("dee@site.test");
        data.GetProperty("company").GetString().Should().Be("Acme");
        data.TryGetProperty("secret", out _).Should().BeFalse("a private attribute is never handed to another plugin");
    }

    private async Task<(string Slug, Guid TenantId)> SiteAsync(CancellationToken ct)
    {
        var admin = fixture.Admin.CreateClient();
        var owner = Guid.NewGuid();
        var slug = "frm-" + Guid.NewGuid().ToString("N")[..8];
        var tenant = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct);
        tenant.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenantId = (await tenant.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("tenantId").GetGuid();

        foreach (var (pluginId, instanceSlug, config) in new[] { ("visitor-auth", "members", VisitorConfig), ("forms", "contact", FormsConfig) })
        {
            var res = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/plugins/instances", owner, slug, body:
                new { pluginId, slug = instanceSlug, name = instanceSlug, config }), ct);
            res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync(ct));
        }
        return (slug, tenantId);
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
