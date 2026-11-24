namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// A desktop agent's credential (ADR 0032): minted by a person for one workspace, it acts as them
/// there, narrowed to some environments and optionally to reads, until it expires or is revoked.
/// Only its SHA-256 is kept; the token itself is shown once, when minted.
///
/// <para>Workspace-wide like memberships, not per environment: <see cref="Environments"/> names the
/// ones it may reach (empty: all the person can).</para>
/// </summary>
public sealed class AgentToken : TenantEntity
{
    public Guid UserId { get; set; }

    /// <summary>What the person called it: "Claude Code on laptop".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Environment names it may reach; empty for every one the person can.</summary>
    public List<string> Environments { get; set; } = [];

    public bool ReadOnly { get; set; }

    /// <summary>Lowercase hex SHA-256 of the whole token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The token's first characters after its prefix, to tell tokens apart in a list.</summary>
    public string Hint { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
