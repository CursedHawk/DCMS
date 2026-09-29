using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Security;
using Microsoft.Extensions.Logging;

namespace Dcms.PluginSdk.Runtime.Platform;

/// <summary>
/// <see cref="IPluginAi"/> over ai-gateway's service endpoint (<c>POST /v1/chat</c>), the path
/// the site chatbot already takes: a client-credentials token scoped <c>dcms.ai</c>, the caller's
/// tenant, and the tenant's own provider and quota on the gateway's side.
/// </summary>
public sealed class PluginAi(
    IPluginContext caller, IServiceTokenProvider tokens, IHttpClientFactory http, ILogger<PluginAi> logger) : IPluginAi
{
    public const int MaxTokensCeiling = 4000;
    private const int MaxPromptChars = 100_000;

    public async Task<AiCompletion> CompleteAsync(AiCompletionRequest input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Prompt) || input.Prompt.Length > MaxPromptChars
            || (input.System?.Length ?? 0) > MaxPromptChars)
        {
            throw new ContractValidationException($"Prompt is required; prompt and system are at most {MaxPromptChars} characters.");
        }

        var token = await tokens.GetTokenAsync("dcms.ai", ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat")
        {
            Content = JsonContent.Create(new
            {
                tenantId = caller.TenantId,
                prompt = input.Prompt,
                system = input.System,
                maxTokens = Math.Clamp(input.MaxTokens, 1, MaxTokensCeiling),
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.CreateClient("ai-gateway").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ContractLimitException("This workspace's AI quota is spent.");
        }
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("ai-gateway /v1/chat returned {Status} for plugin {Plugin} in tenant {TenantId}.",
                (int)response.StatusCode, caller.PluginId, caller.TenantId);
            return new AiCompletion(null);
        }
        var payload = await response.Content.ReadFromJsonAsync<Completion>(ct);
        return new AiCompletion(payload?.Text);
    }

    private sealed record Completion(string? Text);
}
