using System.Text;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;

namespace Dcms.AiGateway.Providers;

/// <summary>
/// Anthropic provider using the official .NET SDK. Adaptive thinking is left at
/// the model default; we read the assembled text blocks from the response.
/// </summary>
public sealed class AnthropicProvider(string apiKey, string? baseUrl) : IChatProvider
{
    public string ProviderId => "anthropic";

    public async Task<string> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var options = new ClientOptions { ApiKey = apiKey };
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            options.BaseUrl = baseUrl;
        }
        var client = new AnthropicClient(options);

        List<MessageParam> messages = [new() { Role = Role.User, Content = request.Prompt }];
        var parameters = string.IsNullOrWhiteSpace(request.System)
            ? new MessageCreateParams { Model = request.Model, MaxTokens = request.MaxTokens, Messages = messages }
            : new MessageCreateParams { Model = request.Model, MaxTokens = request.MaxTokens, Messages = messages, System = request.System };

        var message = await client.Messages.Create(parameters, cancellationToken: ct);

        var sb = new StringBuilder();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var text))
            {
                sb.Append(text.Text);
            }
        }
        return sb.ToString();
    }
}
