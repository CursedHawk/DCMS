using Microsoft.AspNetCore.Identity;

namespace Dcms.Identity.Domain;

/// <summary>
/// Global user account. One identity spans all tenants; per-tenant access is
/// granted through tenant memberships (Phase 3). Visitor accounts on tenant
/// websites are a separate pool (visitors schema, VisitorAuth plugin).
/// </summary>
public sealed class DcmsUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ---- Forgejo mirror ----
    // Each DCMS user has a mirrored Forgejo account so they can clone/pull/push the
    // site repos with their own credentials. The mapping is stored so credential
    // updates (email/password) target the same account deterministically.

    /// <summary>The linked Forgejo username (valid Forgejo handle derived from the email).</summary>
    public string? ForgejoUsername { get; set; }

    /// <summary>The linked Forgejo user id (numeric), for reference/debugging.</summary>
    public long? ForgejoUserId { get; set; }

    /// <summary>When the Forgejo account was last confirmed in sync with this identity.</summary>
    public DateTimeOffset? ForgejoSyncedAt { get; set; }

    /// <summary>
    /// True once a usable git password has been set on the Forgejo account. Password
    /// accounts get one at register/reset; Google-only accounts start without one, so
    /// the Web IDE can prompt them to set a password or add an SSH key.
    /// </summary>
    public bool HasGitPassword { get; set; }
}
