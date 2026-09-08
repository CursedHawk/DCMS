using System.Text.Json.Nodes;
using Dcms.AiGateway.Providers;

namespace Dcms.UnitTests.Ai;

/// <summary>
/// The translation that lets the browser agent run against any OpenAI-compatible provider.
///
/// <para>Worth testing closely because every mistake it can make is silent. The agent loop in
/// the browser reads Anthropic SSE events and decides from them when a tool call is complete;
/// a translation that drops a <c>content_block_stop</c>, or that numbers two blocks the same,
/// or that hands OpenAI a tool result with no <c>tool_call_id</c>, does not throw anywhere — it
/// produces a turn that hangs, or a model that answers as if a tool had never run.</para>
/// </summary>
public class AnthropicOpenAiBridgeTests
{
    // ---------- request ----------

    [Fact]
    public void The_system_prompt_becomes_the_first_message()
    {
        var request = Translate(new JsonObject
        {
            ["system"] = "You are helpful.",
            ["messages"] = new JsonArray(User("hello")),
        });

        var messages = request["messages"]!.AsArray();
        messages[0]!["role"]!.GetValue<string>().Should().Be("system");
        messages[0]!["content"]!.GetValue<string>().Should().Be("You are helpful.");
        messages[1]!["role"]!.GetValue<string>().Should().Be("user");
    }

    [Fact]
    public void A_system_prompt_sent_as_blocks_is_joined()
    {
        // Anthropic allows either form, and the agent uses the block form when it wants cache
        // control on part of the prompt.
        var request = Translate(new JsonObject
        {
            ["system"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = "First." },
                new JsonObject { ["type"] = "text", ["text"] = "Second." }),
            ["messages"] = new JsonArray(User("hi")),
        });

        request["messages"]![0]!["content"]!.GetValue<string>().Should().Be("First.\nSecond.");
    }

    [Fact]
    public void A_tool_definition_moves_from_input_schema_to_parameters()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
        };

        var request = Translate(new JsonObject
        {
            ["messages"] = new JsonArray(User("read it")),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = "read_file",
                ["description"] = "Read a file.",
                ["input_schema"] = schema,
            }),
        });

        var tool = request["tools"]!.AsArray()[0]!;
        tool["type"]!.GetValue<string>().Should().Be("function");
        tool["function"]!["name"]!.GetValue<string>().Should().Be("read_file");
        // The same JSON Schema document, under the name OpenAI gives it.
        tool["function"]!["parameters"]!.ToJsonString().Should().Be(schema.ToJsonString());
    }

    [Fact]
    public void An_assistant_tool_call_becomes_a_tool_call_with_string_arguments()
    {
        var request = Translate(new JsonObject
        {
            ["messages"] = new JsonArray(
                User("read it"),
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = "Looking." },
                        new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = "toolu_1",
                            ["name"] = "read_file",
                            ["input"] = new JsonObject { ["path"] = "src/App.tsx" },
                        }),
                }),
        });

        var assistant = request["messages"]!.AsArray()[1]!;
        assistant["content"]!.GetValue<string>().Should().Be("Looking.");

        var call = assistant["tool_calls"]!.AsArray()[0]!;
        call["id"]!.GetValue<string>().Should().Be("toolu_1");
        call["function"]!["name"]!.GetValue<string>().Should().Be("read_file");
        // A JSON *string*, not an object. Sending the object is the single most common way this
        // translation is written wrong, and the provider answers 400 with nothing useful in it.
        call["function"]!["arguments"]!.GetValue<string>().Should().Be("""{"path":"src/App.tsx"}""");
    }

    [Fact]
    public void A_tool_result_becomes_its_own_message_addressed_to_the_call()
    {
        var request = Translate(new JsonObject
        {
            ["messages"] = new JsonArray(
                User("read it"),
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = "toolu_1",
                        ["content"] = "export function App() {}",
                    }),
                }),
        });

        var result = request["messages"]!.AsArray()[1]!;
        result["role"]!.GetValue<string>().Should().Be("tool");
        result["tool_call_id"]!.GetValue<string>().Should().Be("toolu_1");
        result["content"]!.GetValue<string>().Should().Be("export function App() {}");
    }

    [Fact]
    public void Several_tool_results_in_one_turn_keep_their_own_ids()
    {
        // The agent runs tools in parallel and answers them in one user turn; collapsing them
        // into one message would attach every answer to the first call.
        var request = Translate(new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    ToolResult("toolu_1", "one"),
                    ToolResult("toolu_2", "two")),
            }),
        });

        var messages = request["messages"]!.AsArray();
        messages.Count.Should().Be(2);
        messages.Select(m => m!["tool_call_id"]!.GetValue<string>()).Should().Equal("toolu_1", "toolu_2");
    }

    [Fact]
    public void An_assistant_turn_that_is_only_a_tool_call_has_null_content()
    {
        var request = Translate(new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = "toolu_1",
                    ["name"] = "list",
                    ["input"] = new JsonObject(),
                }),
            }),
        });

        // Null, not "": some servers reject an empty string where they accept an absent one.
        request["messages"]![0]!["content"].Should().BeNull();
    }

    [Fact]
    public void Thinking_blocks_are_dropped_rather_than_approximated()
    {
        // They carry an Anthropic signature nothing else can verify, and no OpenAI-compatible
        // endpoint has an equivalent. Forwarding them as text would put the model's private
        // reasoning into the prompt as if the user had written it.
        var request = Translate(new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "thinking", ["thinking"] = "hmm", ["signature"] = "sig" },
                    new JsonObject { ["type"] = "text", ["text"] = "Done." }),
            }),
        });

        var content = request["messages"]![0]!["content"]!.GetValue<string>();
        content.Should().Be("Done.");
        request.ToJsonString().Should().NotContain("hmm");
    }

    [Fact]
    public void Usage_is_only_asked_for_where_the_option_is_understood()
    {
        var body = new JsonObject { ["messages"] = new JsonArray(User("hi")), ["stream"] = true };

        AnthropicOpenAiBridge.TranslateRequest(body.DeepClone().AsObject(), "m", includeUsageOption: true)
            ["stream_options"]!["include_usage"]!.GetValue<bool>().Should().BeTrue();

        // A stricter server answers 400 for a field it does not know, and losing a metric is a
        // far better outcome than losing the request.
        AnthropicOpenAiBridge.TranslateRequest(body.DeepClone().AsObject(), "m", includeUsageOption: false)
            ["stream_options"].Should().BeNull();
    }

    [Theory]
    [InlineData("tool_calls", "tool_use")]
    [InlineData("length", "max_tokens")]
    [InlineData("stop", "end_turn")]
    [InlineData(null, "end_turn")]
    public void Finish_reasons_map_to_the_stop_reasons_the_loop_reads(string? finish, string expected)
    {
        // `tool_use` is the one that matters: the loop continues only when it sees it, so a
        // wrong mapping ends the turn with a tool call nobody runs.
        AnthropicOpenAiBridge.StopReason(finish).Should().Be(expected);
    }

    // ---------- response stream ----------

    [Fact]
    public void Streamed_text_opens_one_block_and_appends_to_it()
    {
        var translator = new OpenAiStreamTranslator();

        var first = translator.Feed("""{"choices":[{"delta":{"content":"Hel"}}]}""");
        var second = translator.Feed("""{"choices":[{"delta":{"content":"lo"}}]}""");

        first.Should().HaveCount(2, "the first delta opens the block and carries the text");
        first[0].Should().Contain("content_block_start").And.Contain("\"type\":\"text\"");
        first[1].Should().Contain("text_delta").And.Contain("Hel");

        second.Should().ContainSingle().Which.Should().Contain("text_delta").And.Contain("lo");
    }

    [Fact]
    public void A_streamed_tool_call_is_one_block_assembled_across_chunks()
    {
        var translator = new OpenAiStreamTranslator();

        var opening = translator.Feed(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"name":"read","arguments":""}}]}}]}""");
        var args = translator.Feed(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":"}}]}}]}""");

        opening.Should().ContainSingle();
        opening[0].Should().Contain("tool_use").And.Contain("call_a").And.Contain("read");
        args.Should().ContainSingle();
        args[0].Should().Contain("input_json_delta").And.Contain("path");
    }

    [Fact]
    public void Text_and_a_tool_call_get_different_block_indices()
    {
        // The loop stores blocks in an array keyed by index. Two blocks sharing one would make
        // the tool call overwrite the assistant's text, or the reverse.
        var translator = new OpenAiStreamTranslator();
        translator.Feed("""{"choices":[{"delta":{"content":"Looking."}}]}""");
        var tool = translator.Feed(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c","function":{"name":"n","arguments":""}}]}}]}""");

        tool[0].Should().Contain("\"index\":1");
    }

    [Fact]
    public void Two_parallel_tool_calls_get_a_block_each()
    {
        var translator = new OpenAiStreamTranslator();
        translator.Feed(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"x","arguments":""}}]}}]}""");
        translator.Feed(
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"b","function":{"name":"y","arguments":""}}]}}]}""");

        var closing = string.Join("", translator.Finish());
        closing.Should().Contain("\"index\":0").And.Contain("\"index\":1");
    }

    [Fact]
    public void Every_open_block_is_closed_and_the_stop_reason_reported()
    {
        var translator = new OpenAiStreamTranslator();
        translator.Feed("""{"choices":[{"delta":{"content":"hi"}}]}""");
        translator.Feed(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"x","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""");

        var frames = translator.Finish();

        // The loop parses a tool call's accumulated JSON on content_block_stop; without one it
        // holds a call it never dispatches and waits for a turn that has already finished.
        frames.Count(f => f.Contains("content_block_stop")).Should().Be(2);
        frames[^1].Should().Contain("message_delta").And.Contain("tool_use");
    }

    [Fact]
    public void Usage_is_read_off_the_stream()
    {
        var translator = new OpenAiStreamTranslator();
        translator.Feed("""{"choices":[],"usage":{"prompt_tokens":120,"completion_tokens":45}}""");

        translator.PromptTokens.Should().Be(120);
        translator.CompletionTokens.Should().Be(45);
    }

    [Fact]
    public void A_malformed_or_empty_chunk_is_ignored_rather_than_fatal()
    {
        // A keep-alive, the [DONE] sentinel and a truncated chunk are all things a provider
        // sends. None is a reason to tear down a stream the reader is halfway through.
        var translator = new OpenAiStreamTranslator();

        translator.Feed("").Should().BeEmpty();
        translator.Feed("[DONE]").Should().BeEmpty();
        translator.Feed("{not json").Should().BeEmpty();
    }

    [Fact]
    public void Frames_are_shaped_the_way_the_browser_parses_them()
    {
        // The client splits on a blank line and matches `^data: ?(.*)$`. A frame missing either
        // is dropped silently, which presents as an assistant that says nothing.
        var frame = new OpenAiStreamTranslator().Feed("""{"choices":[{"delta":{"content":"x"}}]}""")[0];

        frame.Should().StartWith("event: ").And.EndWith("\n\n");
        frame.Should().Contain("\ndata: {");
    }

    // ---------- helpers ----------

    private static JsonObject Translate(JsonObject anthropic) =>
        AnthropicOpenAiBridge.TranslateRequest(anthropic, "test-model", includeUsageOption: false);

    private static JsonObject User(string text) =>
        new() { ["role"] = "user", ["content"] = text };

    private static JsonObject ToolResult(string id, string content) => new()
    {
        ["type"] = "tool_result",
        ["tool_use_id"] = id,
        ["content"] = content,
    };
}
