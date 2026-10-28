using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dcms.IntegrationTests.Cms;

/// <summary>
/// An app link (ADR 0026, 0028): a development environment's site reads production's published content
/// through its own <c>/api/{slug}</c>, read only; links stay inside one workspace.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class EnvironmentLinkTests(ContentFlowFixture fixture)
{
    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string? slug = null, object? body = null, bool superAdmin = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (superAdmin) req.Headers.Add("X-Test-Roles", "SuperAdmin");
        if (slug is not null) Environments.EnvironmentTarget.Apply(req, slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static HttpRequestMessage Site(HttpMethod method, string slug, string url)
    {
        var req = new HttpRequestMessage(method, url);
        Environments.EnvironmentTarget.Apply(req, slug);
        return req;
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage res, CancellationToken ct)
    {
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));
        return res.Content.Headers.ContentLength == 0 ? default : await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    [DockerFact]
    public async Task Development_reads_productions_published_content_through_a_link()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fixture.Admin.CreateClient();
        var content = fixture.Content.CreateClient();
        var owner = Guid.NewGuid();
        var prod = "link-" + Guid.NewGuid().ToString("N")[..8];
        await Ok(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", owner, body:
            new { slug = prod, name = prod, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }, superAdmin: true), ct), ct);
        var news = (await Ok(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, prod,
            new { pluginId = "blog", slug = "news", name = "News", config = "{}" }), ct), ct)).GetProperty("id").GetGuid();
        var item = (await Ok(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/content", owner, prod,
            new { pluginInstanceId = news, contentType = "post", slug = "hello", data = new { title = "From production" } }), ct), ct)).GetProperty("id").GetGuid();
        await Ok(await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/content/{item}/publish", owner, prod), ct), ct);

        var dev = (await Ok(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/environments", owner, prod, new { name = "development", empty = true }), ct), ct))
            .GetProperty("id").GetGuid();
        var prodId = (await Ok(await admin.SendAsync(Req(HttpMethod.Get, "/api/admin/environments", owner, prod), ct), ct)).GetProperty("workspaceId").GetGuid();
        var devSlug = $"{prod}/development";

        // Without a link, development has no such app.
        (await content.SendAsync(Site(HttpMethod.Get, devSlug, "/api/news/post/hello"), ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A link names an app the target has, in the same workspace.
        (await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/environments/{dev}/links", owner, prod,
            new { kind = "app", targetEnvironmentId = prodId, targetKey = "nope", access = "read" }), ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/environments/{dev}/links", owner, prod,
            new { kind = "app", targetEnvironmentId = Guid.NewGuid(), targetKey = "news", access = "read" }), ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Ok(await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/environments/{dev}/links", owner, prod,
            new { kind = "app", targetEnvironmentId = prodId, targetKey = "news", access = "read" }), ct), ct);

        var served = await Ok(await content.SendAsync(Site(HttpMethod.Get, devSlug, "/api/news/post/hello"), ct), ct);
        served.GetProperty("data").GetProperty("title").GetString().Should().Be("From production");

        // Read only: a write through the link is refused, not sent to production.
        (await content.SendAsync(Site(HttpMethod.Post, devSlug, "/api/news/post"), ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
