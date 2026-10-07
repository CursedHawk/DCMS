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

    /// <summary>Whether people may sign in with an email and password, or only through a provider.</summary>
    public bool PasswordEnabled { get; set; } = true;

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

public enum RealmProviderKind
{
    /// <summary>Google Workspace (or any Google account, without a hosted domain).</summary>
    Google,

    /// <summary>Microsoft Entra ID, one directory.</summary>
    Entra,

    /// <summary>Any OpenID Connect provider, by issuer.</summary>
    Oidc,

    /// <summary>The person's DCMS (platform) account, linked to their account in this realm.</summary>
    Dcms,
}

public enum RealmProvisioning
{
    /// <summary>Only people already invited (or linked) get in.</summary>
    InviteOnly,

    /// <summary>Also anyone the provider vouches for at one of <see cref="RealmProvider.AllowedDomains"/>: their account is created on first sign-in.</summary>
    AllowedDomains,
}

/// <summary>
/// One way into a realm besides a password (ADR 0022): a tenant's own Google, Entra or OIDC
/// client, or DCMS. The client secret is encrypted with Vault Transit (<c>dcms-realm-secrets</c>,
/// identity's alone) and never leaves identity again.
/// </summary>
public sealed class RealmProvider
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Lowercase, unique per realm: the sign-in button's path (<c>/realm/{slug}/sso/{key}</c>).</summary>
    public string Key { get; set; } = string.Empty;

    public RealmProviderKind Kind { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    public string? ClientId { get; set; }

    /// <summary>Generic OIDC: the issuer, whose discovery document is read.</summary>
    public string? Issuer { get; set; }

    /// <summary>Entra: the directory (tenant id or verified domain). Never common/organizations: the directory is what vouches for the email.</summary>
    public string? EntraTenant { get; set; }

    /// <summary>Google: only accounts of this Workspace domain (the <c>hd</c> claim).</summary>
    public string? HostedDomain { get; set; }

    /// <summary>Transit ciphertext of the client secret.</summary>
    public string? SecretCiphertext { get; set; }

    public RealmProvisioning Provisioning { get; set; } = RealmProvisioning.InviteOnly;
    public List<string> AllowedDomains { get; set; } = [];

    /// <summary>Groups an account created through this provider starts in.</summary>
    public List<Guid> DefaultGroups { get; set; } = [];

    /// <summary>The claim carrying the person's groups at the provider (Entra: <c>groups</c>).</summary>
    public string? GroupClaim { get; set; }

    /// <summary>A provider group value → a realm group: kept in step at every sign-in through this provider.</summary>
    public Dictionary<string, Guid> GroupMappings { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
