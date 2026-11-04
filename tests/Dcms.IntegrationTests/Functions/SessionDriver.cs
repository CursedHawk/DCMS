extern alias ScriptRunnerApp;

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ScriptRunnerApp::Dcms.ScriptRunner;

namespace Dcms.IntegrationTests.Functions;

internal sealed record SessionRun(JsonObject Done, List<string> Logs);

/// <summary>
/// Drives one script-runner session as admin-api's FunctionRunner does: sends the start message,
/// answers each host call with <c>broker</c>, collects the log lines and the done message.
/// </summary>
internal static class SessionDriver
{
    public static async Task<SessionRun> RunAsync(ScriptRunnerOptions options, JsonObject start,
        Func<string, JsonNode?, (bool Ok, JsonNode? Result, string? Error)>? broker = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var session = new FunctionSession(Microsoft.Extensions.Options.Options.Create(options), NullLogger<FunctionSession>.Instance);
        var (server, client) = await PairAsync();
        using (server)
        using (client)
        {
            var serving = session.RunAsync(server, ct);
            await Send(client, start, ct);

            var logs = new List<string>();
            JsonObject? done = null;
            while (await Receive(client, ct) is { } message)
            {
                switch (message["type"]!.GetValue<string>())
                {
                    case "call":
                        var (ok, result, error) = (broker ?? ((_, _) => (false, null, "no broker")))(message["op"]!.GetValue<string>(), message["args"]);
                        await Send(client, new JsonObject
                        {
                            ["type"] = "reply", ["id"] = message["id"]!.DeepClone(), ["ok"] = ok, ["result"] = result, ["error"] = error,
                        }, ct);
                        break;
                    case "log":
                        logs.Add($"{message["level"]}: {message["message"]}");
                        break;
                    case "done":
                        done = message;
                        break;
                }
            }
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
            await serving;
            return new SessionRun(done ?? throw new InvalidOperationException("No done message."), logs);
        }
    }

    private static async Task<(WebSocket Server, WebSocket Client)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var tcp = new TcpClient();
            var connect = tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = await listener.AcceptTcpClientAsync();
            await connect;
            return (WebSocket.CreateFromStream(accepted.GetStream(), new WebSocketCreationOptions { IsServer = true }),
                    WebSocket.CreateFromStream(tcp.GetStream(), new WebSocketCreationOptions { IsServer = false }));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static Task Send(WebSocket socket, JsonObject message, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonObject?> Receive(WebSocket socket, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await socket.ReceiveAsync(chunk, ct);
            if (read.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            buffer.Write(chunk, 0, read.Count);
            if (read.EndOfMessage)
            {
                return JsonNode.Parse(buffer.ToArray())!.AsObject();
            }
        }
    }
}
