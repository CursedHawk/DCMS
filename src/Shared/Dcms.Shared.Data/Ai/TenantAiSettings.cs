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
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
