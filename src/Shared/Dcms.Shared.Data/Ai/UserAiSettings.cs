namespace Dcms.Shared.Data.Ai;

/// <summary>
/// Per-user AI configuration, scoped to a (tenant, user) pair. Lets an individual
/// user connect their own Anthropic (or other) API key for the web-IDE agent; the
/// key is stored as Vault Transit ciphertext (never plaintext). Resolution order
/// is user → tenant (<see cref="TenantAiSettings"/>) → platform default, so a user
/// row overrides the tenant row only where it sets a value.
/// </summary>
public sealed class UserAiSettings
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public AiProvider Provider { get; set; } = AiProvider.Inherit;
    public string? Model { get; set; }
    public string? ApiKeyCiphertext { get; set; }
    public string? BaseUrl { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
