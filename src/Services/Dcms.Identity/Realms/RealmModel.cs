namespace Dcms.Identity.Realms;

/// <summary>
/// One tenant's pool of enterprise users (ADR 0022). Its own users, groups and sign-in, kept apart
/// from the platform's <c>DcmsUser</c> accounts by construction: separate tables, a separate cookie
/// and an OIDC client per tenant. An account in one realm means nothing in another.
/// </summary>
public sealed class Realm
{
    /// <summary>The tenant's id; one realm per tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The tenant's slug: where its sign-in pages live (<c>/realm/{slug}/login</c>).</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Shown on the sign-in pages.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The tenant's site hostnames: the edge's sign-in callbacks are registered for these.</summary>
    public List<string> Hosts { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum RealmUserStatus
{
    /// <summary>Created by an invitation that has not been accepted yet: cannot sign in.</summary>
    Invited,
    Active,

    /// <summary>Switched off by an administrator: cannot sign in, and every session ends.</summary>
    Disabled,
}

/// <summary>An enterprise user of one tenant's realm.</summary>
public sealed class RealmUser
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Email { get; set; } = string.Empty;

    /// <summary>Upper-cased email: unique per tenant, never across tenants.</summary>
    public string NormalizedEmail { get; set; } = string.Empty;

    public string? DisplayName { get; set; }
    public RealmUserStatus Status { get; set; } = RealmUserStatus.Invited;

    /// <summary>Null for a user who signs in only through a provider.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Changes whenever credentials or status do: every cookie, token and link minted before goes stale.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public int AccessFailedCount { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSignInAt { get; set; }
}

/// <summary>A named set of a realm's users: what site access rules and roles are granted to.</summary>
public sealed class RealmGroup
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RealmGroupMember
{
    public Guid TenantId { get; set; }
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>A realm user's identity at an external provider (Google, Entra, OIDC, DCMS): how it signs in there.</summary>
public sealed class RealmLogin
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>The realm's provider key, e.g. <c>google</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The subject the provider names the person by.</summary>
    public string ProviderKey { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
