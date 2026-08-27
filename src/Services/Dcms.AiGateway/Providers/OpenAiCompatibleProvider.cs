using System.ClientModel;
using OpenAI;
using OpenAI.Chat;

namespace Dcms.AiGateway.Providers;

/// <summary>
/// OpenAI-compatible provider — covers OpenAI, Ollama and LM Studio. The latter
/// two expose an OpenAI-compatible /v1 endpoint, so the only difference is the
/// base URL (and that they ignore the API key). One implementation, three vendors.
/// </summary>
public sealed class OpenAiCompatibleProvider(string providerId, string apiKey, string? baseUrl) : IChatProvider
{
    public string ProviderId => providerId;

    public async Task<ChatCompletionResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "not-needed" : apiKey);
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            options.Endpoint = new Uri(baseUrl);
        }
        var client = new ChatClient(request.Model, credential, options);

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            messages.Add(ChatMessage.CreateSystemMessage(request.System));
        }
        messages.Add(ChatMessage.CreateUserMessage(request.Prompt));

        var completion = await client.CompleteChatAsync(messages, cancellationToken: ct);
        var parts = completion.Value.Content;
        var usage = completion.Value.Usage;
        return new ChatCompletionResult(
            parts.Count > 0 ? parts[0].Text : string.Empty,
            // Ollama and LM Studio may not populate usage at all; see ChatCompletionResult.
            usage?.InputTokenCount ?? 0,
            usage?.OutputTokenCount ?? 0);
    }
}
