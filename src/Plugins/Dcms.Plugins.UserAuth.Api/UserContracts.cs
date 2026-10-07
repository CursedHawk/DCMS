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

public sealed record PermissionList(IReadOnlyList<string> Permissions);

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

/// <summary>
/// The tenant's enterprise users and groups, for the assistant and for other plugins. Admin
/// plane only: the directory lives in identity, which only admin-api may ask.
/// </summary>
[DcmsContract("users.directory", 1, Description = "The tenant's enterprise users and groups: list, invite, enable or disable, group membership.")]
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
}
