using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.UserAuth.Api;

/// <summary>An enterprise user of the tenant's sites, as other plugins see them.</summary>
public sealed record SiteUser(Guid Id, string Email, string? DisplayName, IReadOnlyList<Guid> Groups);

public sealed record CurrentUser(SiteUser? User);

/// <summary>Who the signed-in enterprise user is — for plugins serving the tenant's sites.</summary>
[DcmsContract("users.identity", 1, Description = "The signed-in enterprise user of the current request, if any.")]
public interface IUserIdentity
{
    /// <summary>Null user for an anonymous request, a visitor, or outside a request.</summary>
    [Operation(OpRisk.Read, Expose = OpExposure.Site, ReturnsExternalText = true,
        Description = "The signed-in user: id, email, name and groups; null when nobody is signed in.")]
    Task<CurrentUser> GetCurrentAsync(CancellationToken ct);
}

/// <param name="Permission">A <see cref="SitePermission"/> key, e.g. <c>dynamic-apps:crm:table:deals:read</c>.</param>
public sealed record PermissionCheck(string Permission);

public sealed record AccessDecision(bool Allowed);

/// <param name="BypassRowAccess">
/// The subset of <paramref name="Permissions"/> held through a role that bypasses row-level access:
/// with these the holder reaches every record; with the rest, a plugin's row rules still pick them.
/// </param>
public sealed record PermissionList(IReadOnlyList<string> Permissions, IReadOnlyList<string>? BypassRowAccess = null);

/// <summary>
/// What the signed-in enterprise user may do: the permissions of the roles granted to them and
/// to their groups. The policy decision point for every plugin that gates its site resources.
/// </summary>
[DcmsContract("users.access", 1, Description = "Whether the signed-in enterprise user holds a site permission.")]
public interface IUserAccess
{
    /// <summary>False for anyone not signed in, and for a key that is not a valid permission.</summary>
    [Operation(OpRisk.Read, Expose = OpExposure.Site,
        Description = "Whether the signed-in user holds a site permission; false when nobody is signed in.")]
    Task<AccessDecision> CheckAsync(PermissionCheck input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site,
        Description = "Every site permission the signed-in user holds; empty when nobody is signed in.")]
    Task<PermissionList> ListPermissionsAsync(CancellationToken ct);
}

public sealed record UserResourceAction(string Action, string Label);

/// <param name="Resource">The resource part of the permission key, e.g. <c>table:deals</c>.</param>
public sealed record UserResource(string Resource, string Label, IReadOnlyList<UserResourceAction> Actions);

/// <summary>One plugin instance's gateable resources; each permission is <c>{Plugin}:{Instance}:{Resource}:{Action}</c>.</summary>
public sealed record UserResourceCatalog(string Plugin, string Instance, string Label, IReadOnlyList<UserResource> Resources);

/// <summary>
/// What a plugin lets a tenant gate for its site users — resources and their actions — so the
/// roles screen can offer them. Open: every plugin with something to gate provides it.
/// </summary>
[DcmsContract("users.resources", 1, Description = "The resources and actions a plugin lets tenants gate for their site users.")]
public interface IUserResources
{
    [Operation(OpRisk.Read, Description = "This instance's gateable resources and their actions.")]
    Task<UserResourceCatalog> ListAsync(CancellationToken ct);
}

public sealed record UserSearch(string? Search = null, int Page = 1, int PageSize = 50);

/// <param name="Status"><c>invited</c>, <c>active</c> or <c>disabled</c>.</param>
public sealed record DirectoryUser(
    Guid Id, string Email, string? DisplayName, string Status, IReadOnlyList<Guid> Groups,
    DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt);

public sealed record DirectoryUserPage(IReadOnlyList<DirectoryUser> Items, int Total);

public sealed record UserInvite(string Email, string? DisplayName = null, IReadOnlyList<Guid>? Groups = null);

public sealed record UserEnabledChange(Guid UserId, bool Enabled);

public sealed record GroupMembership(Guid UserId, Guid GroupId);

public sealed record DirectoryGroup(Guid Id, string Name, string? Description, int Members);

public sealed record DirectoryGroupList(IReadOnlyList<DirectoryGroup> Groups);

/// <summary>Someone was invited to sign in to the tenant's sites (from the console, a flow or the assistant).</summary>
[ContractEvent("user.invited")]
public sealed record UserInvited(Guid UserId, string Email) : IPluginEvent;

/// <summary>An account became active: an invitation accepted, or an account made on first sign-in through a trusted provider.</summary>
[ContractEvent("user.activated")]
public sealed record UserActivated(Guid UserId, string Email) : IPluginEvent;

/// <summary>
/// The tenant's enterprise users and groups, for the assistant and for other plugins. Admin
/// plane only: the directory lives in identity, which only admin-api may ask.
/// </summary>
[DcmsContract("users.directory", 1, Description = "The tenant's enterprise users and groups: list, invite, enable or disable, group membership.",
    Events = [typeof(UserInvited), typeof(UserActivated)])]
public interface IUserDirectory
{
    [Operation(OpRisk.Read, Permission = UserAuthPermissions.UsersRead, Expose = OpExposure.Admin | OpExposure.Ai, ReturnsExternalText = true,
        Description = "Site users, optionally filtered by email or name.")]
    Task<DirectoryUserPage> ListUsersAsync(UserSearch input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = UserAuthPermissions.UsersRead, Expose = OpExposure.Admin | OpExposure.Ai, ReturnsExternalText = true,
        Description = "The groups site users can belong to.")]
    Task<DirectoryGroupList> ListGroupsAsync(CancellationToken ct);

    /// <summary>Emails the invitation; the link itself is never returned here.</summary>
    [Operation(OpRisk.Safe, Permission = UserAuthPermissions.UsersManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Invite someone by email to sign in to the tenant's sites, optionally into groups.")]
    Task<DirectoryUser> InviteAsync(UserInvite input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = UserAuthPermissions.UsersManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Enable or disable a site user. Disabling signs them out everywhere.")]
    Task<DirectoryUser> SetEnabledAsync(UserEnabledChange input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = UserAuthPermissions.UsersManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Add a site user to a group.")]
    Task AddToGroupAsync(GroupMembership input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = UserAuthPermissions.UsersManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Remove a site user from a group.")]
    Task RemoveFromGroupAsync(GroupMembership input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = UserAuthPermissions.UsersRead, Expose = OpExposure.Admin | OpExposure.Ai, ReturnsExternalText = true,
        Description = "One site user by id: email, name, status, groups and last sign-in.")]
    Task<DirectoryUser> GetUserAsync(UserRef input, CancellationToken ct);

    /// <summary>Emails the invitation again; the link itself is never returned here.</summary>
    [Operation(OpRisk.Safe, Permission = UserAuthPermissions.UsersManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Send an invited site user their invitation email again.")]
    Task ResendInviteAsync(UserRef input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = UserAuthPermissions.UsersManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Sign a site user out of every session; they can sign in again.")]
    Task SignOutUserAsync(UserRef input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = UserAuthPermissions.UsersManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Delete a site user for good, with the roles granted to them directly. Prefer disabling when unsure.")]
    Task DeleteUserAsync(UserRef input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = UserAuthPermissions.UsersManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Create a group of site users. Groups are what site rules admit and what roles are granted to.")]
    Task<DirectoryGroup> CreateGroupAsync(GroupWrite input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = UserAuthPermissions.UsersManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Rename a group or change its description.")]
    Task<DirectoryGroup> UpdateGroupAsync(GroupUpdate input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = UserAuthPermissions.UsersManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Delete a group: its members lose the roles granted to it, and site rules naming it admit nobody through it.")]
    Task DeleteGroupAsync(GroupRef input, CancellationToken ct);
}

public sealed record UserRef(Guid UserId);

public sealed record GroupRef(Guid GroupId);

public sealed record GroupWrite(string Name, string? Description = null);

public sealed record GroupUpdate(Guid GroupId, string Name, string? Description = null);

/// <param name="SubjectType"><c>group</c> or <c>user</c>.</param>
public sealed record RoleGrantInfo(Guid Id, string SubjectType, Guid SubjectId);

public sealed record SiteRole(
    Guid Id, string Key, string Name, string? Description, IReadOnlyList<string> Permissions, bool BypassRowAccess,
    IReadOnlyList<RoleGrantInfo> Grants);

public sealed record SiteRoleList(IReadOnlyList<SiteRole> Roles);

public sealed record UserResourceCatalogList(IReadOnlyList<UserResourceCatalog> Catalogs);

/// <param name="Key">Lowercase letters, digits and <c>_</c>, up to 64; unique.</param>
/// <param name="Permissions">Site permissions, <c>{plugin}:{instance}:{resource}:{action}</c>.</param>
public sealed record RoleCreate(string Key, string Name, string? Description = null, IReadOnlyList<string>? Permissions = null,
    bool BypassRowAccess = false);

/// <param name="Permissions">The role's whole permission list: it replaces what the role holds.</param>
/// <param name="BypassRowAccess">Null keeps what the role has.</param>
public sealed record RoleUpdate(Guid RoleId, string Key, string Name, string? Description, IReadOnlyList<string> Permissions,
    bool? BypassRowAccess = null);

public sealed record RoleRef(Guid RoleId);

/// <param name="SubjectType"><c>group</c> or <c>user</c>.</param>
public sealed record RoleGrant(Guid RoleId, string SubjectType, Guid SubjectId);

public sealed record RoleRevoke(Guid RoleId, Guid GrantId);

public sealed record GrantResult(Guid GrantId);

/// <summary>
/// Who may do what on the tenant's sites: roles (named sets of site permissions) and who holds
/// them. A role reaches nobody until it is granted to a group or a user.
/// </summary>
[DcmsContract("users.roles", 1, Description = "Roles of site permissions for the tenant's site users, and who holds them.")]
public interface IUserRoles
{
    [Operation(OpRisk.Read, Permission = UserAuthPermissions.UsersRead, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Every permission a role can hold, by plugin instance: each is {plugin}:{instance}:{resource}:{action}, " +
                      "e.g. dynamic-apps:crm:table:deals:read. Read this before writing a role.")]
    Task<UserResourceCatalogList> ListResourcesAsync(CancellationToken ct);

    [Operation(OpRisk.Read, Permission = UserAuthPermissions.UsersRead, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "The roles, their permissions and the groups and users each is granted to.")]
    Task<SiteRoleList> ListRolesAsync(CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = UserAuthPermissions.AccessManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Create a role with a list of site permissions. It reaches nobody until granted. Least privilege: only what the role needs. " +
                      "Dynamic Apps row rules still narrow a table permission to matching rows unless bypassRowAccess is true; leave it false unless asked.")]
    Task<SiteRole> CreateRoleAsync(RoleCreate input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = UserAuthPermissions.AccessManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Change a role: its permissions are replaced by the list given, for everyone who holds it, on their next request.")]
    Task<SiteRole> UpdateRoleAsync(RoleUpdate input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = UserAuthPermissions.AccessManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Delete a role and every grant of it.")]
    Task DeleteRoleAsync(RoleRef input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = UserAuthPermissions.AccessManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Grant a role to a group (preferred) or a single user. Granting the same role twice changes nothing.")]
    Task<GrantResult> GrantAsync(RoleGrant input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = UserAuthPermissions.AccessManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Take a role back from the group or user of one grant (the grant ids are in the role list).")]
    Task RevokeAsync(RoleRevoke input, CancellationToken ct);
}

public sealed record RealmState(string Slug, IReadOnlyList<string> Hosts, bool PasswordEnabled, bool Ready);

public sealed record PasswordSignIn(bool Enabled);

/// <param name="HasSecret">Whether a client secret is stored; the secret itself is never returned.</param>
/// <param name="CallbackUrl">The redirect URI to register at the provider.</param>
public sealed record SignInProvider(
    string Key, string Kind, string DisplayName, bool Enabled, string? ClientId, bool HasSecret, string? Issuer, string? EntraTenant,
    string? HostedDomain, string Provisioning, IReadOnlyList<string> AllowedDomains, IReadOnlyList<Guid> DefaultGroups,
    string? GroupClaim, IReadOnlyDictionary<string, Guid> GroupMappings, string? CallbackUrl);

public sealed record SignInProviderList(IReadOnlyList<SignInProvider> Providers);

/// <param name="Key">The provider's key, e.g. <c>google</c>; saving under an existing key replaces it.</param>
/// <param name="Kind"><c>google</c>, <c>entra</c> or <c>oidc</c>.</param>
/// <param name="ClientSecret">Write-only: sets or replaces the stored secret; null keeps it.</param>
/// <param name="Provisioning"><c>inviteOnly</c> or <c>allowedDomains</c>.</param>
public sealed record SignInProviderWrite(
    string Key, string Kind, string DisplayName, bool Enabled = true, string? ClientId = null, string? ClientSecret = null,
    string? Issuer = null, string? EntraTenant = null, string? HostedDomain = null, string? Provisioning = null,
    IReadOnlyList<string>? AllowedDomains = null, IReadOnlyList<Guid>? DefaultGroups = null, string? GroupClaim = null,
    IReadOnlyDictionary<string, Guid>? GroupMappings = null);

public sealed record ProviderRef(string Key);

/// <param name="Access"><c>public</c>, <c>signedIn</c> or <c>groups</c>.</param>
/// <param name="Groups">The groups admitted, for <c>groups</c>.</param>
public sealed record SiteRule(string Prefix, string Access, IReadOnlyList<Guid>? Groups = null);

public sealed record SiteRules(Guid SiteId, string Name, bool Spa, IReadOnlyList<string> Hosts, IReadOnlyList<SiteRule> Rules);

public sealed record SiteRulesList(IReadOnlyList<SiteRules> Sites);

/// <param name="Rules">The site's whole rule list, in order: the first matching prefix decides.</param>
public sealed record SiteRulesWrite(Guid SiteId, IReadOnlyList<SiteRule> Rules);

/// <param name="Live">False when saved but the edge could not be told yet: it will be within minutes.</param>
public sealed record RulesSaved(bool Live, string? Note = null);

/// <param name="Access"><c>public</c>, <c>signedIn</c> or <c>permission</c>.</param>
public sealed record ApiAccessRule(Guid InstanceId, string Slug, string Name, string Plugin, string Access, string ReadPermission, string WritePermission);

public sealed record ApiAccessList(IReadOnlyList<ApiAccessRule> Instances);

/// <param name="Access"><c>public</c>, <c>signedIn</c> or <c>permission</c>.</param>
public sealed record ApiAccessChange(Guid InstanceId, string Access);

/// <summary>
/// How people sign in to the tenant's sites and what needs signing in: the realm, its identity
/// providers (secrets write-only), each site's path rules and each app's site API access.
/// </summary>
[DcmsContract("users.realm", 1, Description = "Sign-in for the tenant's sites: passwords, identity providers, site path rules and site API access.")]
public interface IUserRealm
{
    [Operation(OpRisk.Read, Permission = UserAuthPermissions.UsersRead, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "The realm: its hosts, whether passwords are allowed, and whether sign-in is ready.")]
    Task<RealmState> GetRealmAsync(CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = UserAuthPermissions.ProvidersManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Allow or stop signing in with a password. Stopping it leaves only the identity providers.")]
    Task<RealmState> SetPasswordSignInAsync(PasswordSignIn input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = UserAuthPermissions.UsersRead, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "The identity providers (Google, Microsoft Entra, OIDC). Client secrets are never returned, only whether one is stored.")]
    Task<SignInProviderList> ListProvidersAsync(CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = UserAuthPermissions.ProvidersManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Add or replace an identity provider. clientSecret is write-only: give it to set or rotate it, leave it null to keep the stored one; " +
                      "never ask for it to be read back. Returns the callback URL to register at the provider.")]
    Task<SignInProvider> SetProviderAsync(SignInProviderWrite input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = UserAuthPermissions.ProvidersManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Remove an identity provider: nobody can sign in through it any more.")]
    Task RemoveProviderAsync(ProviderRef input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = UserAuthPermissions.UsersRead, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Every site with its hosts and its path rules in order (who may open which pages).")]
    Task<SiteRulesList> ListSiteRulesAsync(CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = UserAuthPermissions.AccessManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Replace a site's path rules as a whole, in order: e.g. [{prefix:'/portal', access:'groups', groups:[…]}, {prefix:'/', access:'public'}]. " +
                      "List them first and keep the ones that should stay.")]
    Task<RulesSaved> SetSiteRulesAsync(SiteRulesWrite input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = UserAuthPermissions.UsersRead, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Who may call each app's site API, and the permissions a role needs when access is 'permission'.")]
    Task<ApiAccessList> ListApiAccessAsync(CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = UserAuthPermissions.AccessManage, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Set who may call one app's site API: public, signedIn, or permission (then only holders of its read/write permission).")]
    Task SetApiAccessAsync(ApiAccessChange input, CancellationToken ct);
}

/// <param name="Ids">These people, when given (up to 50); else a search by email or name.</param>
public sealed record PeopleSearch(string? Search = null, IReadOnlyList<Guid>? Ids = null);

public sealed record Person(Guid Id, string Email, string? DisplayName);

public sealed record PeopleList(IReadOnlyList<Person> Items);

public sealed record GroupName(Guid Id, string Name);

public sealed record GroupNameList(IReadOnlyList<GroupName> Groups);

/// <summary>
/// The tenant's site users and groups as a signed-in site user may see them: to share something
/// with (#297). Only for a signed-in user, who is asked on behalf of; active accounts only, and
/// only who they are — not their status, sign-ins or memberships.
/// </summary>
[DcmsContract("users.people", 1, Description = "The site's people and groups, for the signed-in site user to share with.")]
public interface IUserPeople
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site, ReturnsExternalText = true,
        Description = "Up to 20 site users matching an email or name, or the ones asked for by id; empty when nobody is signed in.")]
    Task<PeopleList> FindAsync(PeopleSearch input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site, ReturnsExternalText = true,
        Description = "The groups site users belong to, by name; empty when nobody is signed in.")]
    Task<GroupNameList> ListGroupsAsync(CancellationToken ct);
}
