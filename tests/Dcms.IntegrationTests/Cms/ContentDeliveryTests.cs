using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dcms.IntegrationTests.Cms;

[Collection(ContentFlowCollection.Name)]
public class ContentDeliveryTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task Published_content_is_served_then_isolated_then_invalidated_on_unpublish()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fixture.Admin.CreateClient();
        var content = fixture.Content.CreateClient();
        var owner = Guid.NewGuid();
        var slug = "flow-" + Guid.NewGuid().ToString("N")[..8];
        var otherSlug = "other-" + Guid.NewGuid().ToString("N")[..8];

        // Provision two tenants (the second is the isolation control).
        await CreateTenant(admin, slug, owner, ct);
        await CreateTenant(admin, otherSlug, Guid.NewGuid(), ct);

        // Enable a blog instance and author + publish a post.
        var instanceId = await CreateInstance(admin, slug, owner, "blog", "news", ct);
        var itemId = await CreateContent(admin, slug, owner, instanceId, "post", "hello",
            new { title = "Hello world", body = "First post." }, ct);
        await Publish(admin, slug, owner, itemId, ct);

        // Delivery: the published post is served (DB read on cache miss).
        var served = await content.SendAsync(TenantReq(slug, "/api/news/post/hello"), ct);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await served.Content.ReadFromJsonAsync<JsonElement>(ct);
        dto.GetProperty("data").GetProperty("title").GetString().Should().Be("Hello world");

        // Isolation: another tenant has no such instance → not found.
        var crossTenant = await content.SendAsync(TenantReq(otherSlug, "/api/news/post/hello"), ct);
        crossTenant.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Unpublish → the outbox → cache-invalidation path eventually 404s.
        await Unpublish(admin, slug, owner, itemId, ct);
        await PollUntil(async () =>
        {
            var res = await content.SendAsync(TenantReq(slug, "/api/news/post/hello"), ct);
            return res.StatusCode == HttpStatusCode.NotFound;
        }, TimeSpan.FromSeconds(20));
    }

    // ---- helpers ----

    private static HttpRequestMessage TenantReq(string slug, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Dcms-Tenant", slug);
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

    private static async Task CreateTenant(HttpClient admin, string slug, Guid owner, CancellationToken ct)
    {
        var res = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task<Guid> CreateInstance(HttpClient admin, string slug, Guid owner, string pluginId, string instanceSlug, CancellationToken ct)
    {
        var res = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/plugins/instances", owner, slug, body:
            new { pluginId, slug = instanceSlug, name = instanceSlug, config = "{}" }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateContent(HttpClient admin, string slug, Guid owner, Guid instanceId, string type, string itemSlug, object data, CancellationToken ct)
    {
        var res = await admin.SendAsync(AdminReq(HttpMethod.Post, "/api/admin/content", owner, slug, body:
            new { pluginInstanceId = instanceId, contentType = type, slug = itemSlug, data }), ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private static async Task Publish(HttpClient admin, string slug, Guid owner, Guid itemId, CancellationToken ct)
    {
        var res = await admin.SendAsync(AdminReq(HttpMethod.Post, $"/api/admin/content/{itemId}/publish", owner, slug), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task Unpublish(HttpClient admin, string slug, Guid owner, Guid itemId, CancellationToken ct)
    {
        var res = await admin.SendAsync(AdminReq(HttpMethod.Post, $"/api/admin/content/{itemId}/unpublish", owner, slug), ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task PollUntil(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("Condition not met within timeout.");
    }
}
