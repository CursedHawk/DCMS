using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dcms.IntegrationTests.Cms;
using Dcms.Shared.Contracts.Events;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using Npgsql;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// LiveChat's off-request work runs on its own contract: the hub publishes
/// <c>live-chat.conversation.started</c>, and the plugin's own subscription turns it into the
/// agents' notification. Anything else can subscribe to the same event.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class LiveChatPluginTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    [DockerFact]
    public async Task A_started_conversation_notifies_agents_and_the_contract_lists_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.NewGuid();
        var slug = "chat-" + Guid.NewGuid().ToString("N")[..8];
        var admin = fixture.Admin.CreateClient();
        var tenant = await Json(await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "", "SuperAdmin",
            new { slug, name = slug, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }), ct), ct);
        var tenantId = tenant.GetProperty("tenantId").GetGuid();
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, slug,
            body: new { pluginId = "live-chat", slug = "chat", name = "Chat", config = "{}" }), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var conversationId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                """INSERT INTO chat.conversations ("Id","TenantId","VisitorName","Status","CreatedAt","LastMessageAt","IsSandbox") VALUES (@id,@t,'Ada',0,now(),now(),false)""", conn);
            cmd.Parameters.AddWithValue("id", conversationId);
            cmd.Parameters.AddWithValue("t", tenantId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // What the hub publishes on a visitor's first message.
        await using (var nats = new NatsClient(fixture.NatsUrl))
        {
            var evt = new PluginEventPublished(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, "live-chat",
                "live-chat.conversation.started", JsonSerializer.Serialize(new { conversationId, visitorName = "Ada" }));
            await nats.CreateJetStreamContext().PublishAsync("plugins.events.live-chat.live-chat.conversation.started", evt, cancellationToken: ct);
        }

        string? kind = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (kind is null && DateTime.UtcNow < deadline)
        {
            var list = await Json(await admin.SendAsync(Req(HttpMethod.Get, "/api/admin/notifications", owner, slug), ct), ct);
            kind = list.GetRawText().Contains("plugin.live-chat.conversation-started", StringComparison.Ordinal)
                ? "plugin.live-chat.conversation-started" : null;
            if (kind is null)
            {
                await Task.Delay(250, ct);
            }
        }
        kind.Should().NotBeNull("the plugin's subscription raises the agents' notification");

        var conversations = await Json(await admin.SendAsync(Req(HttpMethod.Post,
            "/api/admin/contracts/live-chat.conversations@1/List?plane=admin", owner, slug, body: new { }), ct), ct);
        conversations.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid())
            .Should().Contain(conversationId);
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
