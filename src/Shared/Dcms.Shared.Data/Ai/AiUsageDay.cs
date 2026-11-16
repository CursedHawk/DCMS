namespace Dcms.Shared.Data.Ai;

/// <summary>
/// What a workspace spent on AI in one UTC day, per who paid, provider and model. Written by
/// ai-gateway after every model call (one upsert), read by Settings → AI usage.
///
/// <para>Daily rows, not one per call: an agent run is up to hundreds of calls, and the page
/// draws days. The Redis counters in <c>AiQuota</c> enforce the budget; this is the history.</para>
/// </summary>
public sealed class AiUsageDay
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DateOnly Day { get; set; }

    /// <summary>Whose key paid: <see cref="AiUsageSources"/>.</summary>
    public string Source { get; set; } = AiUsageSources.Workspace;

    /// <summary>The workspace provider that served the call; null for the platform default and for a member's own key.</summary>
    public Guid? ProviderId { get; set; }

    /// <summary>The member whose own key paid; set only when <see cref="Source"/> is <c>user</c>.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The provider's name when last used, so a deleted provider still has a label.</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>The provider kind (<see cref="AiProvider"/>), lowercase.</summary>
    public string ProviderKind { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;
    public long Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
}

public static class AiUsageSources
{
    /// <summary>A workspace provider's key (or a workspace provider that carries no key, like a local model).</summary>
    public const string Workspace = "workspace";

    /// <summary>The platform's own key: the workspace configured nothing that pays.</summary>
    public const string Platform = "platform";

    /// <summary>A member's own key, from their personal AI settings.</summary>
    public const string User = "user";
}
