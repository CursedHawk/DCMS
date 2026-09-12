using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dcms.AiGateway.Providers;

/// <summary>
/// Translates one assistant turn between the Anthropic Messages wire format and the
/// OpenAI Chat Completions one, in both directions.
///
/// <para><b>Why translate rather than change the client.</b> The browser agent loop speaks
/// Anthropic: it assembles content blocks, buffers partial tool JSON, and decides when a tool
/// call is complete, from Anthropic's SSE event names. That loop, its approval gate and its
/// transcript are the same code the IDE agent and the ⌘J dock both run. Teaching it a second
/// protocol would mean two parsers, two sets of edge cases and two things to get wrong; putting
/// the translation here means the loop keeps one shape and every OpenAI-compatible server —
/// OpenAI, Ollama, LM Studio, OpenRouter, Groq, Together, vLLM, LiteLLM, Azure — works with it.
/// </para>
///
/// <para><b>What does not survive the trip.</b> Extended thinking is Anthropic's own: the
/// blocks carry a signature that only Anthropic can verify, and no OpenAI-compatible endpoint
/// emits an equivalent. It is dropped from the request rather than approximated, and no
/// thinking blocks come back. Everything the agent actually depends on — text, tool calls, tool
/// results, stop reasons — maps cleanly.</para>
/// </summary>
public static class AnthropicOpenAiBridge
{
    /// <summary>
    /// An Anthropic Messages request as an OpenAI Chat Completions request.
    /// </summary>
    /// <param name="includeUsageOption">
    /// Whether to ask for usage on the stream. OpenAI omits token counts from a streamed
    /// response unless `stream_options.include_usage` is set — but that field is an OpenAI
    /// extension, and a stricter server that does not know it answers 400 rather than ignoring
    /// it. So it is asked for where it is known to work and left off elsewhere, where the cost
    /// of being wrong is a metric rather than a failed request.
    /// </param>
    public static JsonObject TranslateRequest(JsonObject anthropic, string model, bool includeUsageOption)
    {
        var messages = new JsonArray();

        // The system prompt is a top-level field in Anthropic and the first message in OpenAI.
        // It arrives as either a string or a list of text blocks.
        if (SystemText(anthropic["system"]) is { Length: > 0 } system)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        foreach (var entry in anthropic["messages"]?.AsArray() ?? [])
        {
            if (entry is not JsonObject message) continue;
            var role = message["role"]?.GetValue<string>() ?? "user";

            if (message["content"] is JsonValue plain)
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = plain.GetValue<string>() });
                continue;
            }

            if (message["content"] is not JsonArray blocks) continue;

            var text = new List<string>();
            var toolCalls = new JsonArray();

            foreach (var block in blocks.OfType<JsonObject>())
            {
                switch (block["type"]?.GetValue<string>())
                {
                    case "text":
                        if (block["text"]?.GetValue<string>() is { Length: > 0 } t) text.Add(t);
                        break;

                    case "tool_use":
                        toolCalls.Add(new JsonObject
                        {
                            ["id"] = block["id"]?.GetValue<string>(),
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = block["name"]?.GetValue<string>(),
                                // OpenAI carries the arguments as a JSON *string*, not an object.
                                ["arguments"] = (block["input"] ?? new JsonObject()).ToJsonString(),
                            },
                        });
                        break;

                    case "tool_result":
                        // Its own message in OpenAI, addressed to the call it answers. Emitted
                        // immediately so several results in one Anthropic user turn keep their
                        // order and each stays attached to its own tool_call_id.
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = block["tool_use_id"]?.GetValue<string>(),
                            ["content"] = ToolResultText(block["content"]),
                        });
                        break;

                    // "thinking" and anything else: see the class remarks.
                }
            }

            if (text.Count > 0 || toolCalls.Count > 0)
            {
                var translated = new JsonObject { ["role"] = role };
                // Null rather than "", because an assistant turn that is only a tool call has no
                // text and some servers reject an empty string where they accept null.
                translated["content"] = text.Count > 0 ? string.Join("\n", text) : null;
                if (toolCalls.Count > 0) translated["tool_calls"] = toolCalls;
                messages.Add(translated);
            }
        }

        var request = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = anthropic["stream"]?.GetValue<bool>() ?? false,
        };

        if (anthropic["max_tokens"]?.GetValue<int>() is { } maxTokens)
        {
            request["max_tokens"] = maxTokens;
        }
        if (anthropic["temperature"] is JsonValue temperature)
        {
            request["temperature"] = temperature.DeepClone();
        }

        if (anthropic["tools"]?.AsArray() is { Count: > 0 } tools)
        {
            var translated = new JsonArray();
            foreach (var tool in tools.OfType<JsonObject>())
            {
                translated.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool["name"]?.GetValue<string>(),
                        ["description"] = tool["description"]?.GetValue<string>(),
                        // Anthropic calls the JSON Schema `input_schema`; OpenAI calls it
                        // `parameters`. The schema itself is the same document.
                        ["parameters"] = (tool["input_schema"] ?? new JsonObject()).DeepClone(),
                    },
                });
            }
            request["tools"] = translated;

            if (ToolChoice(anthropic["tool_choice"]) is { } choice) request["tool_choice"] = choice;
        }

        if (includeUsageOption && request["stream"]!.GetValue<bool>())
        {
            request["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        return request;
    }

    /// <summary>Anthropic's stop reasons, from OpenAI's finish reasons.</summary>
    public static string StopReason(string? finishReason) => finishReason switch
    {
        "tool_calls" or "function_call" => "tool_use",
        "length" => "max_tokens",
        "content_filter" => "refusal",
        _ => "end_turn",
    };

    private static string? SystemText(JsonNode? system) => system switch
    {
        JsonValue value => value.GetValue<string>(),
        JsonArray blocks => string.Join(
            "\n",
            blocks.OfType<JsonObject>()
                .Select(b => b["text"]?.GetValue<string>())
                .Where(t => !string.IsNullOrEmpty(t))),
        _ => null,
    };

    /// <summary>
    /// A tool result's content. Anthropic allows a bare string or a list of blocks; OpenAI's
    /// `tool` message takes a string, so a list is flattened to its text.
    /// </summary>
    private static string ToolResultText(JsonNode? content) => content switch
    {
        JsonValue value => value.ToString(),
        JsonArray blocks => string.Join(
            "\n",
            blocks.OfType<JsonObject>()
                .Select(b => b["text"]?.GetValue<string>() ?? b.ToJsonString())),
        null => string.Empty,
        _ => content.ToJsonString(),
    };

    private static JsonNode? ToolChoice(JsonNode? anthropic)
    {
        if (anthropic is not JsonObject choice) return null;
        return choice["type"]?.GetValue<string>() switch
        {
            "any" => JsonValue.Create("required"),
            "none" => JsonValue.Create("none"),
            "tool" => new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = choice["name"]?.GetValue<string>() },
            },
            _ => JsonValue.Create("auto"),
        };
    }
}

/// <summary>
/// Turns an OpenAI streamed completion into the Anthropic SSE events the browser agent loop
/// reads: <c>content_block_start</c>, <c>content_block_delta</c>, <c>content_block_stop</c> and
/// a closing <c>message_delta</c> carrying the stop reason.
///
/// <para><b>Stateful by necessity.</b> OpenAI streams a tool call as an id and name in one
/// chunk and its arguments as string fragments across many, keyed by an index of its own; the
/// agent loop expects one Anthropic block per call, opened once, appended to, and closed. This
/// keeps that mapping. Blocks are numbered in the order they first appear, because that is the
/// order the loop assembles them in.</para>
///
/// <para>Usage is captured on the way past for the same reason the Anthropic path scans for it:
/// this is the most expensive endpoint in the platform, and a cost dashboard that cannot see
/// the agent is a cost dashboard with a hole in it.</para>
/// </summary>
public sealed class OpenAiStreamTranslator
{
    private readonly Dictionary<int, int> _toolBlocks = [];
    private int _nextBlock;
    private int? _textBlock;
    private string _finishReason = "end_turn";

    public long PromptTokens { get; private set; }
    public long CompletionTokens { get; private set; }

    /// <summary>
    /// Translates one `data:` payload. Returns the Anthropic SSE frames to write, which may be
    /// none — a keep-alive or a chunk carrying nothing but usage produces no client-visible
    /// event.
    /// </summary>
    public IReadOnlyList<string> Feed(string data)
    {
        if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") return [];

        JsonObject chunk;
        try
        {
            chunk = JsonNode.Parse(data) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            // A malformed chunk is the provider's problem, not a reason to tear down a stream
            // the reader is halfway through.
            return [];
        }

        var frames = new List<string>();

        if (chunk["usage"] is JsonObject usage)
        {
            PromptTokens = Math.Max(PromptTokens, usage["prompt_tokens"]?.GetValue<long>() ?? 0);
            CompletionTokens = Math.Max(CompletionTokens, usage["completion_tokens"]?.GetValue<long>() ?? 0);
        }

        if (chunk["choices"]?.AsArray().FirstOrDefault() is not JsonObject choice) return frames;

        if (choice["finish_reason"]?.GetValue<string>() is { Length: > 0 } finish)
        {
            _finishReason = AnthropicOpenAiBridge.StopReason(finish);
        }

        if (choice["delta"] is not JsonObject delta) return frames;

        if (delta["content"]?.GetValue<string>() is { Length: > 0 } text)
        {
            if (_textBlock is null)
            {
                _textBlock = _nextBlock++;
                frames.Add(Frame("content_block_start", new JsonObject
                {
                    ["type"] = "content_block_start",
                    ["index"] = _textBlock,
                    ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" },
                }));
            }

            frames.Add(Frame("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = _textBlock,
                ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text },
            }));
        }

        foreach (var call in delta["tool_calls"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var openAiIndex = call["index"]?.GetValue<int>() ?? 0;

            if (!_toolBlocks.TryGetValue(openAiIndex, out var block))
            {
                block = _nextBlock++;
                _toolBlocks[openAiIndex] = block;
                frames.Add(Frame("content_block_start", new JsonObject
                {
                    ["type"] = "content_block_start",
                    ["index"] = block,
                    ["content_block"] = new JsonObject
                    {
                        ["type"] = "tool_use",
                        // A provider that streams no id still has to produce a call the loop can
                        // answer, because the tool result is addressed to this id.
                        ["id"] = call["id"]?.GetValue<string>() ?? $"call_{block}",
                        ["name"] = call["function"]?["name"]?.GetValue<string>() ?? "",
                        ["input"] = new JsonObject(),
                    },
                }));
            }

            if (call["function"]?["arguments"]?.GetValue<string>() is { Length: > 0 } fragment)
            {
                frames.Add(Frame("content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = block,
                    ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = fragment },
                }));
            }
        }

        return frames;
    }

    /// <summary>
    /// Closes every block that was opened and reports the stop reason.
    ///
    /// <para>Called once, when the upstream stream ends — including when it ends badly. The
    /// agent loop keys on `content_block_stop` to parse a tool call's accumulated JSON, so a
    /// stream that stops without one leaves a call assembled but never dispatched, and the loop
    /// waits forever.</para>
    /// </summary>
    public IReadOnlyList<string> Finish()
    {
        var frames = new List<string>();

        for (var index = 0; index < _nextBlock; index++)
        {
            frames.Add(Frame("content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = index,
            }));
        }

        /*
         * Report BOTH sides of the usage, not just the completion.
         *
         * This used to emit `output_tokens` only. The prompt side was read into
         * `PromptTokens` for the server's own Prometheus counter and then never put on the
         * wire, so a browser talking to an OpenAI-compatible provider could see what a turn
         * produced but not what it cost to send — and the IDE's per-run benchmark had to record
         * the input as *unknown* rather than risk averaging a missing number in as free.
         *
         * Anthropic reports the prompt side once in `message_start`; there is no such frame in
         * this translation, so it rides along here instead. A reader taking the maximum per
         * field (which both the browser loop and `AnthropicUsageScanner` do) is unaffected by
         * seeing it on a later frame than Anthropic would have sent it.
         */
        frames.Add(Frame("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = _finishReason, ["stop_sequence"] = null },
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = PromptTokens,
                ["output_tokens"] = CompletionTokens,
            },
        }));

        return frames;
    }

    /// <summary>An error the reader should see rather than a stream that simply stops.</summary>
    public static string ErrorFrame(string message) => Frame("error", new JsonObject
    {
        ["type"] = "error",
        ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = message },
    });

    private static string Frame(string eventName, JsonObject payload) =>
        $"event: {eventName}\ndata: {payload.ToJsonString()}\n\n";
}
