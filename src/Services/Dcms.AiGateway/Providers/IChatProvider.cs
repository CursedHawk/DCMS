namespace Dcms.AiGateway.Providers;

public sealed record ChatRequest(string Model, string? System, string Prompt, int MaxTokens = 8192);

/// <summary>
/// One completion, plus what it cost. Token counts come back alongside the text rather than
/// being estimated later because only the provider knows them: every vendor tokenises
/// differently, and a locally re-counted approximation would put a number on the cost
/// dashboard that never reconciles with the invoice it exists to predict.
///
/// <para>Both counts are zero when the provider did not report usage — some OpenAI-compatible
/// servers (Ollama, LM Studio) omit it. Zero is honest here: it means "not reported", and the
/// tokens counter simply does not move, rather than moving by a made-up amount.</para>
/// </summary>
public sealed record ChatCompletionResult(string Text, long PromptTokens, long CompletionTokens);

/// <summary>
/// Provider-agnostic single-shot completion. Concrete providers (Anthropic,
/// OpenAI-compatible) are constructed per call with the resolved model, key and
/// base URL. JSON-schema constraints are applied at the prompt level by the
/// caller, keeping the provider surface uniform across vendors.
/// </summary>
public interface IChatProvider
{
    string ProviderId { get; }

    Task<ChatCompletionResult> CompleteAsync(ChatRequest request, CancellationToken ct = default);
}
