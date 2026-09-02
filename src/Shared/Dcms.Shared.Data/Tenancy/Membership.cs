namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// Links a global user to a tenant. UserId references identity.asp_net_users
/// across schemas (no DB FK — different DbContext); validated at the app layer.
/// </summary>
public sealed class TenantMembership : TenantEntity
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<MemberRole> Roles { get; set; } = [];
}

/// <summary>A custom role within a tenant; carries a set of permission keys.</summary>
public sealed class TenantRole : TenantEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsSystem { get; set; }

    public List<TenantRolePermission> Permissions { get; set; } = [];
    public List<MemberRole> Members { get; set; } = [];
}

public sealed class TenantRolePermission : TenantEntity
{
    public Guid TenantRoleId { get; set; }
    public string Permission { get; set; } = string.Empty;
}

/// <summary>Join row assigning a tenant role to a membership.</summary>
public sealed class MemberRole : TenantEntity
{
    public Guid MembershipId { get; set; }
    public Guid TenantRoleId { get; set; }

    public TenantMembership? Membership { get; set; }
    public TenantRole? Role { get; set; }
}

/// <summary>
/// A pending invitation for an email to join a tenant with a set of roles.
/// The raw token is emailed; only its hash is stored.
/// </summary>
public sealed class Invitation : TenantEntity
{
    public string Email { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string RoleIdsCsv { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the "this invitation lapsed" notification was raised. Expiry is otherwise a
    /// purely passive property — <see cref="IsPending"/> computes it at read time and no job
    /// has ever run over this table — so a sweeper needs somewhere to record that it has
    /// already reported a given invitation, or every poll would notify again.
    /// </summary>
    public DateTimeOffset? ExpiredNotifiedAt { get; set; }

    public bool IsPending => AcceptedAt is null && ExpiresAt > DateTimeOffset.UtcNow;
}
