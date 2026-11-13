namespace Dcms.Shared.Data.Ai;

public enum AiProvider
{
    /// <summary>Use the platform-configured global default provider.</summary>
    Inherit,
    Anthropic,
    OpenAi,
    Ollama,
    LmStudio,
}

/// <summary>
/// Per-tenant AI configuration. The API key is stored as Vault Transit
/// ciphertext (never plaintext); local providers (Ollama, LM Studio) use BaseUrl
/// and no key. Provider=Inherit falls back to the global Vault KV defaults.
/// </summary>
public sealed class TenantAiSettings
{
    public Guid TenantId { get; set; }
    public AiProvider Provider { get; set; } = AiProvider.Inherit;
    public string? Model { get; set; }
    public string? ApiKeyCiphertext { get; set; }
    public string? BaseUrl { get; set; }
    /// <summary>
    /// How many model turns a site agent may take in one run. Null is
    /// <see cref="DefaultAgentMaxTurns"/>. Set from the platform console only: the tenant's own
    /// settings endpoint never writes it.
    /// </summary>
    public int? AgentMaxTurns { get; set; }

    /// <summary>
    /// How many model turns the console assistant may take for one question. Null is
    /// <see cref="DefaultAssistantMaxTurns"/>. Platform console only.
    /// </summary>
    public int? AssistantMaxTurns { get; set; }

    /// <summary>
    /// The prompt size, in tokens, at which an agent's conversation is compacted into a summary of
    /// the whole of it. Null is <see cref="DefaultCompactAtTokens"/>. Platform console only.
    /// </summary>
    public int? CompactAtTokens { get; set; }

    /// <summary>
    /// The tenant's overrides of the agents' system prompts, a JSON object keyed by the catalogue
    /// in <c>packages/core/src/aiPrompts.ts</c>. Null is none. Platform console only, like
    /// <see cref="AgentMaxTurns"/>.
    /// </summary>
    public string? PromptOverridesJson { get; set; }

    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public const int DefaultAgentMaxTurns = 100;
    public const int MaxAgentMaxTurns = 500;
    public const int DefaultAssistantMaxTurns = 10;
    public const int MaxAssistantMaxTurns = 100;
    public const int DefaultCompactAtTokens = 100_000;
    public const int MinCompactAtTokens = 20_000;
    public const int MaxCompactAtTokens = 800_000;
}
