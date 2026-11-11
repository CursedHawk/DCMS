using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Tenancy;

namespace Dcms.IntegrationTests.Cms;

[Collection(AdminApiCollection.Name)]
public class ScheduledPublishTests(AdminApiFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task Scheduled_content_publishes_when_due()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fixture.Factory.CreateClient();
        var owner = Guid.NewGuid();
        var slug = "sched-" + Guid.NewGuid().ToString("N")[..8];

        // Provision tenant + blog instance + a draft post.
        (await client.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", owner, "SuperAdmin",
            body: new { slug, name = slug, ownerUserId = owner, ownerEmail = "o@dcms.test" }, asSuperAdmin: true), ct))
            .EnsureSuccessStatusCode();

        var instanceId = await CreatedId(client, Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, slug: slug,
            body: new { pluginId = "blog", slug = "news", name = "News", config = "{}" }), ct);
        var itemId = await CreatedId(client, Req(HttpMethod.Post, "/api/admin/content", owner, slug: slug,
            body: new { pluginInstanceId = instanceId, contentType = "post", slug = "soon", data = new { title = "Soon" } }), ct);

        // Schedule ~2s out; the fast test scheduler (1s poll) should publish it.
        var schedule = await client.SendAsync(Req(HttpMethod.Post, $"/api/admin/content/{itemId}/schedule", owner, slug: slug,
            body: new { publishAt = DateTimeOffset.UtcNow.AddSeconds(2) }), ct);
        schedule.StatusCode.Should().Be(HttpStatusCode.OK);

        await PollUntil(async () =>
        {
            var res = await client.SendAsync(Req(HttpMethod.Get, $"/api/admin/content/{itemId}", owner, slug: slug), ct);
            if (res.StatusCode != HttpStatusCode.OK) return false;
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            return json.GetProperty("status").GetString() == "Published";
        }, TimeSpan.FromSeconds(20));
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string roles = "", string? slug = null, object? body = null, bool asSuperAdmin = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (asSuperAdmin) req.Headers.Add("X-Test-Roles", "SuperAdmin");
        else if (!string.IsNullOrEmpty(roles)) req.Headers.Add("X-Test-Roles", roles);
        if (slug is not null) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<Guid> CreatedId(HttpClient client, HttpRequestMessage req, CancellationToken ct)
    {
        var res = await client.SendAsync(req, ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await res.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private static async Task PollUntil(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(500);
        }
        throw new TimeoutException("Scheduled item did not publish in time.");
    }
}
