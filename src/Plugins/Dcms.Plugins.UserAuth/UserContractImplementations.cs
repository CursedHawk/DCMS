using System.Text.Json;
using Dcms.Plugins.UserAuth.Api;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Contracts.Realms;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.UserAuth;
using Dcms.Shared.Security.Realms;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.UserAuth;

/// <summary>
/// The signed-in user of the current request. Outside a request (a job, an event handler) there
/// is nobody signed in, which is the correct answer rather than an error.
/// </summary>
public sealed class UserIdentity(IHttpContextAccessor http) : IUserIdentity
{
    public async Task<CurrentUser> GetCurrentAsync(CancellationToken ct) =>
        new(http.HttpContext is { } request ? await request.CurrentUserAsync() : null);
}

/// <summary>
/// The same user as a <c>visitors.identity@1</c> visitor, so what already works for visitors —
/// "own records" in Dynamic Apps, prefilled forms — works for enterprise users unchanged. Only
/// what a realm token says: no profile attributes.
/// </summary>
public sealed class UserVisitorIdentity(IHttpContextAccessor http) : IVisitorIdentity
{
    public async Task<CurrentVisitor> GetCurrentAsync(CancellationToken ct) =>
        new(http.HttpContext is { } request && await request.CurrentUserAsync() is { } user
            ? new VisitorProfile(user.Id, user.Email, user.DisplayName, new Dictionary<string, JsonElement>(), DateTimeOffset.MinValue)
            : null);
}

/// <summary>
/// What the signed-in user may do: the union of the permissions of every role granted to them or
/// to one of their groups. Groups are the ones in their token, so a membership change reaches
/// them with their next token (minutes); a role or grant change reaches them on their next request.
/// </summary>
public sealed class UserAccess(IHttpContextAccessor http, IPluginContext context, UserAuthDbContext db) : IUserAccess
{
    private const string PermissionsItem = "dcms.user-auth.permissions";

    public async Task<AccessDecision> CheckAsync(PermissionCheck input, CancellationToken ct) =>
        new(SitePermission.IsValid(input.Permission) && (await PermissionsAsync(ct)).Contains(input.Permission));

    public async Task<PermissionList> ListPermissionsAsync(CancellationToken ct) =>
        new((await PermissionsAsync(ct)).Order(StringComparer.Ordinal).ToList());

    private async Task<HashSet<string>> PermissionsAsync(CancellationToken ct)
    {
        if (http.HttpContext is not { } request || await request.CurrentUserAsync() is not { } user)
        {
            return [];
        }
        if (request.Items.TryGetValue(PermissionsItem, out var cached) && cached is HashSet<string> known)
        {
            return known;
        }
        var permissions = await ForAsync(db, context.TenantId, user.Id, user.Groups, ct);
        request.Items[PermissionsItem] = permissions;
        return permissions;
    }

    /// <summary>The permissions one user holds, directly or through their groups.</summary>
    public static async Task<HashSet<string>> ForAsync(UserAuthDbContext db, Guid tenantId, Guid userId, IReadOnlyList<Guid> groups, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(tenantId);
        var roleIds = db.Grants
            .Where(g => (g.SubjectType == GrantSubject.User && g.SubjectId == userId)
                        || (g.SubjectType == GrantSubject.Group && groups.Contains(g.SubjectId)))
            .Select(g => g.RoleId);
        var lists = await db.Roles.Where(r => roleIds.Contains(r.Id)).Select(r => r.Permissions).ToListAsync(ct);
        return lists.SelectMany(p => p).ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// The tenant's users and groups through identity's realm admin API. admin-api only: that is the
/// one service allowed to ask identity, so on the site plane every operation refuses.
/// </summary>
public sealed class UserDirectory(IPluginContext context, IServiceProvider services) : IUserDirectory
{
    private RealmAdminClient Realms => services.GetService<RealmAdminClient>()
        ?? throw new InvalidOperationException("The user directory is available on the admin plane only.");

    public async Task<DirectoryUserPage> ListUsersAsync(UserSearch input, CancellationToken ct)
    {
        var query = $"/users?page={Math.Max(input.Page, 1)}&pageSize={Math.Clamp(input.PageSize, 1, 100)}"
                    + (string.IsNullOrWhiteSpace(input.Search) ? "" : $"&search={Uri.EscapeDataString(input.Search)}");
        var page = await Call(() => Realms.CallAsync<RealmUserPage>(context.TenantId, HttpMethod.Get, query, null, ct));
        return new DirectoryUserPage(page.Items.Select(ToUser).ToList(), page.Total);
    }

    public async Task<DirectoryGroupList> ListGroupsAsync(CancellationToken ct)
    {
        var groups = await Call(() => Realms.CallAsync<List<RealmGroupInfo>>(context.TenantId, HttpMethod.Get, "/groups", null, ct));
        return new DirectoryGroupList(groups.Select(g => new DirectoryGroup(g.Id, g.Name, g.Description, g.Members)).ToList());
    }

    public async Task<DirectoryUser> InviteAsync(UserInvite input, CancellationToken ct)
    {
        var result = await Call(() => Realms.CallAsync<RealmInviteResult>(context.TenantId, HttpMethod.Post, "/users/invite",
            new RealmInvite(input.Email, input.DisplayName, input.Groups), ct));
        return ToUser(result.User);
    }

    public async Task<DirectoryUser> SetEnabledAsync(UserEnabledChange input, CancellationToken ct) =>
        ToUser(await Call(() => Realms.CallAsync<RealmUserInfo>(context.TenantId, HttpMethod.Patch, $"/users/{input.UserId}",
            new RealmUserPatch(null, input.Enabled ? "active" : "disabled"), ct)));

    public Task AddToGroupAsync(GroupMembership input, CancellationToken ct) =>
        Call(() => Realms.CallAsync(context.TenantId, HttpMethod.Put, $"/groups/{input.GroupId}/members/{input.UserId}", null, ct));

    public Task RemoveFromGroupAsync(GroupMembership input, CancellationToken ct) =>
        Call(() => Realms.CallAsync(context.TenantId, HttpMethod.Delete, $"/groups/{input.GroupId}/members/{input.UserId}", null, ct));

    private static DirectoryUser ToUser(RealmUserInfo u) =>
        new(u.Id, u.Email, u.DisplayName, u.Status, u.Groups, u.CreatedAt, u.LastSignInAt);

    /// <summary>Identity's refusal of the input (a 4xx) is the caller's to fix, so it surfaces as a validation error.</summary>
    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (RealmAdminException e) when (e.Status is >= 400 and < 500)
        {
            throw new ContractValidationException(e.Message);
        }
    }

    private static Task Call(Func<Task> call) => Call(async () =>
    {
        await call();
        return true;
    });
}
