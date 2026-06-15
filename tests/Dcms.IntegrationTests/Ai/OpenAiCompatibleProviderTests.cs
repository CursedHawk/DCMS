extern alias AiGatewayApp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using AiGatewayApp::Dcms.AiGateway.Providers;

namespace Dcms.IntegrationTests.Ai;

/// <summary>
/// Verifies the OpenAI-compatible provider against an in-process stub OpenAI
/// server (covers OpenAI / Ollama / LM Studio, which share the wire format).
/// Runs locally — no Docker, no real API key.
/// </summary>
public class OpenAiCompatibleProviderTests
{
    [Fact]
    public async Task Completes_chat_against_an_openai_compatible_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        string? capturedAuth = null;

        // Minimal stub serving the OpenAI chat-completions shape on a dynamic port.
        var app = WebApplication.CreateBuilder().Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapPost("/v1/chat/completions", (HttpContext http) =>
        {
            capturedAuth = http.Request.Headers.Authorization.ToString();
            return Results.Json(new
            {
                id = "chatcmpl-test",
                @object = "chat.completion",
                created = 0,
                model = "stub-model",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = "HELLO FROM STUB" },
                        finish_reason = "stop",
                    },
                },
                usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
            });
        });
        await app.StartAsync(ct);
        try
        {
            var baseUrl = app.Urls.First().TrimEnd('/') + "/v1";
            var provider = new OpenAiCompatibleProvider("openai", "secret-key", baseUrl);

            var text = await provider.CompleteAsync(
                new ChatRequest("stub-model", "You are a test.", "Say hello", MaxTokens: 64), ct);

            text.Should().Be("HELLO FROM STUB");
            // The provider must forward the API key (and never log it — checked by review).
            capturedAuth.Should().Contain("secret-key");
        }
        finally
        {
            await app.StopAsync(ct);
        }
    }

    [Fact]
    public void Provider_id_reflects_the_configured_vendor()
    {
        new OpenAiCompatibleProvider("ollama", "", "http://localhost:11434/v1").ProviderId.Should().Be("ollama");
        new OpenAiCompatibleProvider("lmstudio", "", "http://localhost:1234/v1").ProviderId.Should().Be("lmstudio");
    }
}
