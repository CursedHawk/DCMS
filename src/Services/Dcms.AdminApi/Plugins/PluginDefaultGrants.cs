using Dcms.AdminApi.Tenancy;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Plugins;

/// <summary>
/// Gives the built-in Member role the permissions a plugin marks <see cref="PermissionDefinition.GrantToMembers"/>,
/// once, when a tenant installs its first instance of that plugin. Only then: a tenant that later
/// takes a permission away from Member has decided, and a second instance must not undo that.
/// </summary>
public static class PluginDefaultGrants
{
    public static async Task<int> ApplyAsync(TenancyDbContext db, Guid tenantId, PluginManifest manifest, CancellationToken ct)
    {
        var keys = manifest.Permissions.Where(p => p.GrantToMembers)
            .Select(p => PluginPermissions.Resolve(manifest.Id, p.Action))
            .ToList();
        if (keys.Count == 0)
        {
            return 0;
        }
        var member = await db.TenantRoles.Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.IsSystem && r.Name == TenantProvisioning.MemberRoleName, ct);
        if (member is null)
        {
            return 0;
        }
        var missing = keys.Except(member.Permissions.Select(p => p.Permission)).ToList();
        foreach (var key in missing)
        {
            db.TenantRolePermissions.Add(new TenantRolePermission
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                TenantRoleId = member.Id,
                Permission = key,
            });
        }
        await db.SaveChangesAsync(ct);
        return missing.Count;
    }
}
