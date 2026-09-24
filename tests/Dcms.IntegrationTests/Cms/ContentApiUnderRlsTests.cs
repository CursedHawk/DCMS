using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dcms.IntegrationTests.Cms;

/// <summary>
/// ADR 0015 phase 4: content-api's public writes and its background indexer, run as
/// <c>dcms_app</c> (see <see cref="ContentFlowFixture"/>). Each check reads back what it wrote,
/// because under RLS a lost tenant is an empty result or a refused write, not an error page:
/// a status-code check would pass a visitor who registers and then can never log in.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public class ContentApiUnderRlsTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task A_visitor_registers_and_then_logs_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var (slug, owner) = await TenantAsync(ct);
        await InstanceAsync(slug, owner, "visitor-auth", "members", "{}", ct);
        var content = fixture.Content.CreateClient();
        var credentials = new { email = $"{Guid.NewGuid():N}@visitor.test", password = "Correct-horse-9", displayName = "V" };

        (await content.SendAsync(Req(HttpMethod.Post, slug, "/api/members/register", credentials), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var login = await content.SendAsync(Req(HttpMethod.Post, slug, "/api/members/login", credentials), ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK, "the account the register call wrote must be readable as that tenant");
    }

    [DockerFact]
    public async Task A_form_submission_is_stored_for_its_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (slug, owner) = await TenantAsync(ct);
        var instanceId = await InstanceAsync(slug, owner, "forms", "contact",
            """{"forms":[{"name":"hello","fields":[{"name":"message","required":true}]}]}""", ct);
        var marker = "rls-" + Guid.NewGuid().ToString("N")[..8];

        var submitted = await fixture.Content.CreateClient().SendAsync(
            Req(HttpMethod.Post, slug, "/api/contact/forms/hello", new { message = marker }), ct);
        ((int)submitted.StatusCode).Should().BeInRange(200, 299);

        var listed = await fixture.Admin.CreateClient().SendAsync(
            AdminReq(HttpMethod.Get, $"/api/admin/forms/{instanceId}/submissions", owner, slug), ct);
        (await listed.Content.ReadAsStringAsync(ct)).Should().Contain(marker);
    }

    /// <summary>The background path: SearchIndexer acts as the event's tenant, off any request.</summary>
    [DockerFact]
    public async Task Published_content_is_indexed_and_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var (slug, owner) = await TenantAsync(ct);
        await InstanceAsync(slug, owner, "search", "find", "{}", ct);
        var blog = await InstanceAsync(slug, owner, "blog", "news", "{}", ct);
        var word = "zebra" + Guid.NewGuid().ToString("N")[..6];
        var admin = fixture.Admin.CreateClient();

        var created = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/content", owner, slug,
            new { pluginInstanceId = blog, contentType = "post", slug = "indexed", data = new { title = word, body = word } }), ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var itemId = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        (await admin.SendAsync(AdminReq(HttpMethod.Post, $"/api/admin/content/{itemId}/publish", owner, slug), ct))
            .EnsureSuccessStatusCode();

        var content = fixture.Content.CreateClient();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        var total = 0;
        while (DateTime.UtcNow < deadline && total == 0)
        {
            var res = await content.SendAsync(Req(HttpMethod.Get, slug, $"/api/find/search?q={word}", null), ct);
            if (res.IsSuccessStatusCode)
            {
                total = (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("total").GetInt32();
            }
            if (total == 0) await Task.Delay(500, ct);
        }
        total.Should().Be(1, "the indexer wrote the document as the event's tenant, and search reads it as the request's");
    }

    private async Task<(string Slug, Guid Owner)> TenantAsync(CancellationToken ct)
    {
        var slug = "rls-" + Guid.NewGuid().ToString("N")[..8];
        var owner = Guid.NewGuid();
        var res = await fixture.Admin.CreateClient().SendAsync(AdminReq(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "",
            new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }, "SuperAdmin"), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (slug, owner);
    }

    private async Task<Guid> InstanceAsync(string slug, Guid owner, string pluginId, string instanceSlug, string config, CancellationToken ct)
    {
        var res = await fixture.Admin.CreateClient().SendAsync(AdminReq(HttpMethod.Post, "/api/admin/plugins/instances", owner, slug,
            new { pluginId, slug = instanceSlug, name = instanceSlug, config }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync(ct));
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private static HttpRequestMessage Req(HttpMethod method, string slug, string url, object? body)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static HttpRequestMessage AdminReq(HttpMethod method, string url, Guid sub, string slug, object? body = null, string roles = "")
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (roles.Length > 0) req.Headers.Add("X-Test-Roles", roles);
        if (slug.Length > 0) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }
}
