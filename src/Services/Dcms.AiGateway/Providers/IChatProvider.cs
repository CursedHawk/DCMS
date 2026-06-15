namespace Dcms.AiGateway.Providers;

public sealed record ChatRequest(string Model, string? System, string Prompt, int MaxTokens = 8192);

/// <summary>
/// Provider-agnostic single-shot completion. Concrete providers (Anthropic,
/// OpenAI-compatible) are constructed per call with the resolved model, key and
/// base URL. JSON-schema constraints are applied at the prompt level by the
/// caller, keeping the provider surface uniform across vendors.
/// </summary>
public interface IChatProvider
{
    string ProviderId { get; }

    Task<string> CompleteAsync(ChatRequest request, CancellationToken ct = default);
}
