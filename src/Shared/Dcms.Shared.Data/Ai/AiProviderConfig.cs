namespace Dcms.Shared.Data.Ai;

/// <summary>
/// One AI provider a workspace has configured. A workspace may have several (say its Anthropic
/// account and a local Ollama); one is the default, and a member picks another when starting an
/// agent. With none, the platform's default provider serves the workspace.
///
/// <para>The API key is Vault Transit ciphertext, never plaintext, and never leaves the server.
/// Local providers (Ollama, LM Studio) use <see cref="BaseUrl"/> and no key. <see cref="Provider"/>
/// may be <see cref="AiProvider.Inherit"/>: the platform's provider kind with this row's model,
/// URL or key, which is what a single-provider workspace configured that way had before.</para>
/// </summary>
public sealed class AiProviderConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>What members see in the picker: "Anthropic", "Local Llama".</summary>
    public string Name { get; set; } = string.Empty;

    public AiProvider Provider { get; set; } = AiProvider.Anthropic;
    public string? Model { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKeyCiphertext { get; set; }

    /// <summary>Serves every call that picked no provider. At most one per workspace (a partial unique index).</summary>
    public bool IsDefault { get; set; }

    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public const int MaxName = 80;
    public const int MaxPerTenant = 20;
}
