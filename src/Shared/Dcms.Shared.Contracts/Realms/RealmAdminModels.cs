namespace Dcms.Shared.Contracts.Realms;

// The wire records of identity's realm admin API (ADR 0022): identity serves them, admin-api's
// RealmAdminClient sends and reads them. One definition, so the two cannot drift apart.

/// <param name="PasswordEnabled">Null leaves it as it is (new realms allow passwords).</param>
public sealed record RealmUpsert(string Slug, string Name, IReadOnlyList<string>? Hosts, bool? PasswordEnabled = null);

/// <param name="ClientReady">False until identity holds the edge's master secret: the realm exists, but no site can sign anyone in yet.</param>
public sealed record RealmInfo(Guid TenantId, string Slug, string Name, IReadOnlyList<string> Hosts, string ClientId, bool ClientReady,
    bool PasswordEnabled);

/// <param name="ClientSecret">Write-only: set or replace it; null keeps the stored one.</param>
public sealed record RealmProviderWrite(
    string Kind, string DisplayName, bool Enabled = true, string? ClientId = null, string? ClientSecret = null,
    string? Issuer = null, string? EntraTenant = null, string? HostedDomain = null, string? Provisioning = null,
    IReadOnlyList<string>? AllowedDomains = null, IReadOnlyList<Guid>? DefaultGroups = null, string? GroupClaim = null,
    IReadOnlyDictionary<string, Guid>? GroupMappings = null);

/// <param name="CallbackUrl">What to register at the provider as the redirect URI; null for DCMS.</param>
public sealed record RealmProviderInfo(
    string Key, string Kind, string DisplayName, bool Enabled, string? ClientId, bool HasSecret, string? Issuer, string? EntraTenant,
    string? HostedDomain, string Provisioning, IReadOnlyList<string> AllowedDomains, IReadOnlyList<Guid> DefaultGroups,
    string? GroupClaim, IReadOnlyDictionary<string, Guid> GroupMappings, string? CallbackUrl);

public sealed record RealmUserInfo(
    Guid Id, string Email, string? DisplayName, string Status, IReadOnlyList<Guid> Groups,
    bool HasPassword, bool LockedOut, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt);

public sealed record RealmUserPage(IReadOnlyList<RealmUserInfo> Items, int Total, int Page, int PageSize);

public sealed record RealmInvite(string Email, string? DisplayName, IReadOnlyList<Guid>? Groups);

/// <param name="InviteUrl">The link the invitation email carries, for an administrator to pass on when mail does not arrive.</param>
public sealed record RealmInviteResult(RealmUserInfo User, string InviteUrl);

/// <param name="Status"><c>active</c> or <c>disabled</c>; an invited account becomes active only by accepting.</param>
public sealed record RealmUserPatch(string? DisplayName, string? Status);

public sealed record RealmGroupWrite(string Name, string? Description);

public sealed record RealmGroupInfo(Guid Id, string Name, string? Description, int Members);
