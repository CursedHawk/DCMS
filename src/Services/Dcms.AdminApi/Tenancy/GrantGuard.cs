using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Enforces "you cannot grant a permission you do not hold" for tenant role and membership
/// changes. Without it, a holder of <c>members:manage</c> could assign themselves the Owner
/// role (every permission) and purge the workspace, and a holder of <c>roles:manage</c> could
/// widen a role beyond their own rights. (SEC-06)
///
/// <para>The rule is expressed against the caller's <b>effective</b> permission set: a role or a
/// permission list may be granted only when it is a subset of what the caller already has. An
/// Owner (who holds every permission) is therefore unrestricted by construction, and a platform
/// SuperAdmin is exempt entirely.</para>
/// </summary>
public static class GrantGuard
{
    /// <summary>
    /// The permissions the caller is allowed to hand out — their own effective set — or
    /// <c>null</c> when the caller is unrestricted (a platform SuperAdmin).
    /// </summary>
    public static async Task<IReadOnlySet<string>?> GrantableAsync(
        CurrentUser me, ITenantContext tenant, TenancyPermissionResolver resolver, CancellationToken ct)
    {
        if (me.IsSuperAdmin)
        {
            return null;
        }
        var tenantId = tenant.TenantId!.Value;
        return await resolver.GetPermissionsAsync(tenantId, me.RequireUserId(), ct);
    }

    /// <summary>True when every wanted permission is one the caller holds (or the caller is
    /// unrestricted).</summary>
    public static bool MayGrant(IReadOnlySet<string>? grantable, IEnumerable<string> wanted) =>
        grantable is null || wanted.All(grantable.Contains);

    /// <summary>The permission strings a role carries, honouring the tenant query filter.</summary>
    public static Task<List<string>> RolePermissionsAsync(TenancyDbContext db, Guid roleId, CancellationToken ct) =>
        db.TenantRolePermissions
            .Where(p => p.TenantRoleId == roleId)
            .Select(p => p.Permission)
            .ToListAsync(ct);
}
