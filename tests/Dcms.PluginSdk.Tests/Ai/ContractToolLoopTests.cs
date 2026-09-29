using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime.Ai;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.PluginSdk.Tests.Contracts;

namespace Dcms.PluginSdk.Tests.Ai;

public class ContractToolLoopTests
{
    private static string Sse(params object[] events) =>
        string.Concat(events.Select(e => $"event: x\ndata: {JsonSerializer.Serialize(e)}\n\n"));

    private static readonly string ToolUseTurn = Sse(
        new { type = "message_start" },
        new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } },
        new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Let me check. " } },
        new { type = "content_block_stop", index = 0 },
        new { type = "content_block_start", index = 1, content_block = new { type = "tool_use", id = "call_1", name = "crew_members_list", input = new { } } },
        new { type = "content_block_delta", index = 1, delta = new { type = "input_json_delta", partial_json = "{\"page\":" } },
        new { type = "content_block_delta", index = 1, delta = new { type = "input_json_delta", partial_json = "1}" } },
        new { type = "content_block_stop", index = 1 },
        new { type = "message_delta", delta = new { stop_reason = "tool_use" } });

    private static readonly string AnswerTurn = Sse(
        new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } },
        new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Ada plays tonight." } },
        new { type = "content_block_stop", index = 0 },
        new { type = "message_delta", delta = new { stop_reason = "end_turn" } });

    [Fact]
    public async Task Reassembles_text_and_tool_input_from_the_stream()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ToolUseTurn));

        var (content, stop) = await AnthropicStream.ReadAsync(stream, TestContext.Current.CancellationToken);

        stop.Should().Be("tool_use");
        content[0]!["text"]!.GetValue<string>().Should().Be("Let me check. ");
        content[1]!["name"]!.GetValue<string>().Should().Be("crew_members_list");
        content[1]!["input"]!["page"]!.GetValue<int>().Should().Be(1);
    }

    private sealed class Gateway(params string[] turns) : HttpMessageHandler
    {
        private int _next;
        public List<JsonObject> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(turns[Math.Min(_next++, turns.Length - 1)], Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private static readonly AiTool Members = new(
        "crew_members_list", "Members", new JsonObject { ["type"] = "object" }, "roster.members@1", "List", "crew", ReturnsExternalText: false);

    [Fact]
    public async Task Runs_the_requested_tool_then_returns_the_answer()
    {
        var gateway = new Gateway(ToolUseTurn, AnswerTurn);
        JsonNode? ranWith = null;

        var reply = await ContractToolLoop.RunAsync(
            new HttpClient(gateway) { BaseAddress = new Uri("http://ai-gateway") }, "token", Guid.NewGuid(), "system",
            new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Who plays tonight?" }),
            [Members],
            (_, input) => { ranWith = input; return Task.FromResult("""{"items":[{"name":"Ada"}]}"""); },
            maxRounds: 3, maxTokens: 800, TestContext.Current.CancellationToken);

        reply.Should().Be("Ada plays tonight.");
        ranWith!["page"]!.GetValue<int>().Should().Be(1);

        var second = gateway.Requests[1]["request"]!["messages"]!.AsArray();
        second[^1]!["content"]![0]!["type"]!.GetValue<string>().Should().Be("tool_result");
        second[^1]!["content"]![0]!["tool_use_id"]!.GetValue<string>().Should().Be("call_1");
    }

    [Fact]
    public async Task Stops_offering_tools_after_the_round_limit()
    {
        var gateway = new Gateway(ToolUseTurn, ToolUseTurn, AnswerTurn);

        await ContractToolLoop.RunAsync(
            new HttpClient(gateway) { BaseAddress = new Uri("http://ai-gateway") }, "token", Guid.NewGuid(), "system",
            new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "?" }),
            [Members], (_, _) => Task.FromResult("[]"),
            maxRounds: 1, maxTokens: 800, TestContext.Current.CancellationToken);

        gateway.Requests.Should().HaveCount(2);
        gateway.Requests[0]["request"]!["tools"].Should().NotBeNull();
        gateway.Requests[1]["request"]!["tools"].Should().BeNull("the last round must answer with what it has");
    }

    [Fact]
    public void The_site_chatbot_plane_serves_read_only_site_and_ai_operations()
    {
        var descriptor = Runtime.Contracts.ContractDescriptorBuilder.Build(typeof(IGreeter));

        // Greet: Read, Site|Ai. SetGreeting: Safe, Admin.
        ContractDispatcher.IsExposed(descriptor.FindOperation("Greet")!, ContractPlane.SiteAi).Should().BeTrue();
        ContractDispatcher.IsExposed(descriptor.FindOperation("SetGreeting")!, ContractPlane.SiteAi).Should().BeFalse();
    }

    [Theory]
    [InlineData(new[] { "crew", "members", "List" }, "crew_members_list")]
    [InlineData(new[] { "my-site", "profiles", "GetPublic" }, "my-site_profiles_get_public")]
    public void Tool_names_follow_provider_rules(string[] parts, string expected) =>
        ContractToolLoop.ToolName(parts).Should().Be(expected);
}
