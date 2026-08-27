extern alias AiGatewayApp;
using System.Text;
using AiGatewayApp::Dcms.AiGateway.Providers;

namespace Dcms.IntegrationTests.Ai;

/// <summary>
/// The IDE agent's token accounting is read off a byte stream by hand rather than from a parsed
/// response, because the endpoint that carries it is a reverse proxy with nothing to parse.
/// That makes these the tests that decide whether the AI cost dashboard is telling the truth.
/// </summary>
public class AnthropicUsageScannerTests
{
    // A trimmed but faithful Anthropic SSE stream: input_tokens announced once up front,
    // output_tokens climbing in message_delta events until the final one.
    private const string SseStream =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-opus-4-8\"," +
        "\"usage\":{\"input_tokens\":1543,\"output_tokens\":1}}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":57}}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":212}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    [Fact]
    public async Task Reads_usage_from_a_streaming_response_and_copies_it_through_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var scanner = new AnthropicUsageScanner();
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(SseStream));
        using var destination = new MemoryStream();

        await scanner.CopyAsync(source, destination, ct);

        scanner.PromptTokens.Should().Be(1543);
        // The last message_delta wins, not the sum of them: Anthropic's output_tokens is
        // cumulative, so adding the deltas up would report 270 for a 212-token answer.
        scanner.CompletionTokens.Should().Be(212);

        // The proxy's contract is that the browser gets exactly what Anthropic sent.
        Encoding.UTF8.GetString(destination.ToArray()).Should().Be(SseStream);
    }

    [Fact]
    public async Task Reads_usage_from_a_non_streaming_json_response()
    {
        var ct = TestContext.Current.CancellationToken;
        const string json =
            "{\"id\":\"msg_2\",\"type\":\"message\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]," +
            "\"usage\":{\"input_tokens\":12,\"output_tokens\":34}}";

        var scanner = new AnthropicUsageScanner();
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var destination = new MemoryStream();

        await scanner.CopyAsync(source, destination, ct);

        scanner.PromptTokens.Should().Be(12);
        scanner.CompletionTokens.Should().Be(34);
    }

    [Fact]
    public void Finds_a_count_split_across_a_chunk_boundary()
    {
        // The real read loop hands over whatever the socket produced, which is not aligned to
        // anything. Fed one byte at a time, every count is split at every possible point —
        // if the carry-over is wrong, this cannot pass by luck.
        var scanner = new AnthropicUsageScanner();
        var bytes = Encoding.UTF8.GetBytes(SseStream);
        foreach (var b in bytes)
        {
            scanner.Feed([b]);
        }

        scanner.PromptTokens.Should().Be(1543);
        scanner.CompletionTokens.Should().Be(212);
    }

    [Fact]
    public void Reports_zero_rather_than_guessing_when_usage_is_absent()
    {
        var scanner = new AnthropicUsageScanner();
        scanner.Feed(Encoding.UTF8.GetBytes("data: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\"}}"));

        scanner.PromptTokens.Should().Be(0);
        scanner.CompletionTokens.Should().Be(0);
    }
}
