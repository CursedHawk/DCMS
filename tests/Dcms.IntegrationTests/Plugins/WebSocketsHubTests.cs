using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dcms.IntegrationTests.Cms;
using Dcms.Plugins.WebSockets;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// The WebSockets plugin's site hub (ADR 0037), spoken to as a browser does: SignalR's JSON
/// protocol over a raw WebSocket. What it must never do is deliver across tenants, admit a page
/// from another origin, keep serving a disabled instance or pass a payload its channel refuses.
/// </summary>
[Collection(ContentFlowCollection.Name)]
public sealed class WebSocketsHubTests(ContentFlowFixture fixture)
{
    private static readonly Guid SuperAdmin = Guid.NewGuid();

    private const string Config = """
        { "channels": [
          { "name": "chat", "keyed": true, "subscribe": "public", "publish": "subscribers",
            "schema": { "type": "object", "properties": { "text": { "type": "string" } }, "required": ["text"], "additionalProperties": false } },
          { "name": "news", "subscribe": "public", "publish": "none" }
        ] }
        """;

    [DockerFact]
    public async Task Messages_reach_this_tenants_subscribers_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ownerA, tenantA, idA) = await TenantWithInstanceAsync(ct);
        var (_, tenantB, _) = await TenantWithInstanceAsync(ct);

        await using var alice = await Hub.ConnectAsync(fixture, tenantA, "a.example", "https://a.example", ct);
        await using var bob = await Hub.ConnectAsync(fixture, tenantA, "a.example", "https://a.example", ct);
        await using var mallory = await Hub.ConnectAsync(fixture, tenantB, "b.example", "https://b.example", ct);
        foreach (var hub in new[] { alice, bob, mallory })
        {
            (await hub.InvokeAsync("Subscribe", ct, "chat", "room-1")).Should().BeNull();
        }

        (await alice.InvokeAsync("Publish", ct, "chat", "room-1", JsonNode.Parse("""{ "text": "hi" }"""))).Should().BeNull();

        var received = await bob.NextMessageAsync(ct);
        received![0]!.GetValue<string>().Should().Be("chat");
        received[1]!.GetValue<string>().Should().Be("room-1");
        received[2]!["text"]!.GetValue<string>().Should().Be("hi");
        (await mallory.NextMessageAsync(ct, TimeSpan.FromSeconds(1))).Should().BeNull("the same slug in another tenant is another hub group");

        (await alice.InvokeAsync("Publish", ct, "chat", "room-1", JsonNode.Parse("""{ "nope": 1 }"""))).Should().Contain("does not match");
        (await alice.InvokeAsync("Publish", ct, "news", null, JsonNode.Parse("{}"))).Should().Contain("Only the server");
        (await alice.InvokeAsync("Subscribe", ct, "chat", null)).Should().Contain("keyed");
        (await alice.InvokeAsync("Subscribe", ct, "missing", null)).Should().Contain("Unknown channel");

        // Disabled: open connections are refused at their next call.
        (await fixture.Admin.CreateClient().SendAsync(Req(HttpMethod.Post, $"/api/admin/plugins/instances/{idA}/disable", ownerA, tenantA), ct))
            .IsSuccessStatusCode.Should().BeTrue();
        var refused = await RetryAsync(() => alice.InvokeAsync("Subscribe", ct, "chat", "room-2"), ct);
        // The hub answers and drops the connection: either may reach the client first.
        (refused == Hub.Closed || refused!.Contains("no longer available")).Should().BeTrue(refused ?? "no refusal");
    }

    [DockerFact]
    public async Task Only_the_sites_own_pages_may_connect()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, tenant, _) = await TenantWithInstanceAsync(ct);

        var other = async () => await Hub.ConnectAsync(fixture, tenant, "a.example", "https://evil.example", ct);
        await other.Should().ThrowAsync<Exception>("a page on another site would ride this site's session cookie");
        var console = async () => await Hub.ConnectAsync(fixture, tenant, siteHost: null, "https://a.example", ct);
        await console.Should().ThrowAsync<Exception>("without X-Dcms-Site the request did not come for a tenant site");
        var unknown = async () => await Hub.ConnectAsync(fixture, tenant, "a.example", "https://a.example", ct, instance: "nope");
        await unknown.Should().ThrowAsync<Exception>();

        await using var ok = await Hub.ConnectAsync(fixture, tenant, "a.example", "https://a.example", ct);
        (await ok.InvokeAsync("Subscribe", ct, "news", null)).Should().BeNull();
    }

    private static async Task<string?> RetryAsync(Func<Task<string?>> call, CancellationToken ct)
    {
        // The enabled-instance cache may answer a moment longer than the database.
        for (var i = 0; ; i++)
        {
            var error = await call();
            if (error is not null || i >= 20) return error;
            await Task.Delay(500, ct);
        }
    }

    private async Task<(Guid Owner, string Tenant, Guid InstanceId)> TenantWithInstanceAsync(CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        var tenant = "ws-" + Guid.NewGuid().ToString("N")[..8];
        var admin = fixture.Admin.CreateClient();
        (await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/tenants", SuperAdmin, "",
            new { slug = tenant, name = tenant, ownerUserId = owner, ownerEmail = $"{owner:N}@dcms.test" }, "SuperAdmin"), ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await admin.SendAsync(Req(HttpMethod.Post, "/api/admin/plugins/instances", owner, tenant,
            new { pluginId = WebSocketsPlugin.PluginId, slug = "rt", name = "Realtime", description = "Live.", config = Config }), ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        return (owner, tenant, id);
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, Guid sub, string slug, object? body = null, string roles = "")
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Test-Sub", sub.ToString());
        req.Headers.Add("X-Test-Email", $"{sub:N}@dcms.test");
        if (roles.Length > 0) req.Headers.Add("X-Test-Roles", roles);
        if (slug.Length > 0) req.Headers.Add("X-Dcms-Tenant", slug);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    /// <summary>A browser's side of the hub: the handshake, invocations and the server's message calls.</summary>
    private sealed class Hub : IAsyncDisposable
    {
        private const char Rs = '\u001e';
        private readonly WebSocket socket;
        private readonly Channel<JsonArray> messages = Channel.CreateUnbounded<JsonArray>();
        private readonly Dictionary<string, TaskCompletionSource<string?>> pending = [];
        private readonly CancellationTokenSource stop = new();
        private int nextId;
        private bool closed;

        /// <summary>What an invocation answers once the hub has dropped the connection.</summary>
        public const string Closed = "(connection closed)";

        private Hub(WebSocket socket) => this.socket = socket;

        public static async Task<Hub> ConnectAsync(ContentFlowFixture fixture, string tenant, string? siteHost, string origin,
            CancellationToken ct, string instance = "rt")
        {
            var client = fixture.Content.Server.CreateWebSocketClient();
            client.ConfigureRequest = r =>
            {
                r.Headers["X-Dcms-Tenant"] = tenant;
                if (siteHost is not null)
                {
                    r.Headers["X-Dcms-Site"] = Guid.NewGuid().ToString();
                    r.Headers["X-Dcms-Site-Host"] = siteHost;
                }
                r.Headers.Origin = origin;
            };
            var socket = await client.ConnectAsync(new Uri($"ws://localhost{WebSocketsPlugin.HubPath}?instance={instance}"), ct);
            var hub = new Hub(socket);
            await hub.SendAsync(new JsonObject { ["protocol"] = "json", ["version"] = 1 }, ct);
            var handshake = await hub.ReadFramesAsync(ct);
            if (handshake.Count == 0 || handshake[0]["error"] is not null)
            {
                throw new InvalidOperationException("The hub refused the connection.");
            }
            _ = hub.PumpAsync(handshake.Skip(1).ToList());
            // The hub reads invocations only once OnConnectedAsync admitted the connection; a
            // refusal aborts it instead, so an answer is the admission.
            if (await hub.InvokeAsync("Unsubscribe", ct, "news", null) == Closed)
            {
                throw new InvalidOperationException("The hub closed the connection.");
            }
            return hub;
        }

        /// <summary>Null when the invocation completed, else its error.</summary>
        public async Task<string?> InvokeAsync(string target, CancellationToken ct, params object?[] args)
        {
            var id = (++nextId).ToString();
            var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pending)
            {
                if (closed) return Closed;
                pending[id] = done;
            }
            try
            {
                await SendAsync(new JsonObject
                {
                    ["type"] = 1,
                    ["invocationId"] = id,
                    ["target"] = target,
                    ["arguments"] = new JsonArray(args.Select(a => a is JsonNode n ? n.DeepClone() : JsonValue.Create(a as string)).ToArray()),
                }, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return Closed;
            }
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }

        public async Task<JsonArray?> NextMessageAsync(CancellationToken ct, TimeSpan? wait = null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(wait ?? TimeSpan.FromSeconds(10));
            try
            {
                return await messages.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }

        private Task SendAsync(JsonNode message, CancellationToken ct) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString() + Rs), WebSocketMessageType.Text, true, ct);

        private async Task<List<JsonNode>> ReadFramesAsync(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            var text = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return [];
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);
            return text.ToString().Split(Rs, StringSplitOptions.RemoveEmptyEntries).Select(f => JsonNode.Parse(f)!).ToList();
        }

        private async Task PumpAsync(List<JsonNode> first)
        {
            try
            {
                for (var frames = first; ; frames = await ReadFramesAsync(stop.Token))
                {
                    if (frames.Count == 0 && socket.State != WebSocketState.Open) return;
                    foreach (var frame in frames)
                    {
                        switch (frame["type"]?.GetValue<int>())
                        {
                            case 7:
                                return;
                            case 1 when frame["target"]?.GetValue<string>() == WebSocketsHub.MessageMethod:
                                messages.Writer.TryWrite(frame["arguments"]!.AsArray());
                                break;
                            case 3:
                                TaskCompletionSource<string?>? done;
                                lock (pending) pending.Remove(frame["invocationId"]!.GetValue<string>(), out done);
                                done?.TrySetResult(frame["error"]?.GetValue<string>());
                                break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // closed or aborted
            }
            finally
            {
                lock (pending)
                {
                    closed = true;
                    foreach (var done in pending.Values) done.TrySetResult(Closed);
                    pending.Clear();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                catch (Exception)
                {
                    // already gone
                }
            }
            socket.Dispose();
        }
    }
}
