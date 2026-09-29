using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.Shared.Contracts.Events;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// A content plugin's data and lifecycle as its .Api describes them: publishing a blog post
/// raises <c>blog.post.published</c> on the plugin event bus, and the typed
/// <c>blog.posts@1</c> contract serves it to the site.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class ContentEventBridgeTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task Publishing_a_post_raises_the_blogs_typed_event_and_the_contract_serves_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.NewGuid();
        var tenant = "bridge-" + Guid.NewGuid().ToString("N")[..8];
        var admin = fixture.Admin.CreateClient();
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug = tenant, name = tenant, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var instance = await Json(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            body: new { pluginId = "blog", slug = "news", name = "News", config = "{}" }), ct), ct);
        var instanceId = instance.GetProperty("id").GetGuid();
        var item = await Json(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/content", owner, tenant, body: new
        {
            pluginInstanceId = instanceId, contentType = "post", slug = "hello",
            data = new { title = "Hello", body = "<p>Hi</p>", tags = new[] { "news" } },
        }), ct), ct);
        var itemId = item.GetProperty("id").GetGuid();

        (await admin.SendAsync(Req(HttpMethod.Post, $"/api/admin/content/{itemId}/publish", owner, tenant), ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var evt = await NextEventAsync("plugins.events.blog.blog.post.published", itemId, ct);
        evt.PublisherPluginId.Should().Be("blog");
        var payload = JsonSerializer.Deserialize<JsonElement>(evt.PayloadJson);
        payload.GetProperty("instanceId").GetGuid().Should().Be(instanceId);
        payload.GetProperty("slug").GetString().Should().Be("hello");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/news/_contracts/blog.posts@1/List")
        {
            Content = JsonContent.Create(new { page = 1, pageSize = 10 }),
        };
        req.Headers.Add("X-Dcms-Tenant", tenant);
        var page = await Json(await fixture.Content.CreateClient().SendAsync(req, ct), ct);
        var first = page.GetProperty("items")[0];
        first.GetProperty("slug").GetString().Should().Be("hello");
        first.GetProperty("data").GetProperty("title").GetString().Should().Be("Hello");
        first.GetProperty("data").GetProperty("tags")[0].GetString().Should().Be("news");
    }

    private async Task<PluginEventPublished> NextEventAsync(string subject, Guid itemId, CancellationToken ct)
    {
        await using var nats = new NatsClient(fixture.NatsUrl);
        var js = nats.CreateJetStreamContext();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var consumer = await js.CreateOrderedConsumerAsync("PLUGIN_EVENTS",
                new NatsJSOrderedConsumerOpts { FilterSubjects = [subject] }, ct);
            await foreach (var msg in consumer.FetchNoWaitAsync<PluginEventPublished>(new NatsJSFetchOpts { MaxMsgs = 100 }, cancellationToken: ct))
            {
                if (msg.Data is { } e && e.PayloadJson.Contains(itemId.ToString(), StringComparison.Ordinal))
                {
                    return e;
                }
            }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"No {subject} event for {itemId}.");
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res, CancellationToken ct)
    {
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string slug, string roles = "", object? body = null)
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
