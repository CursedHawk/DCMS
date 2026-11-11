using Dcms.AdminApi.Tenancy;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Plugins;

/// <summary>
/// Plugin permissions are held per instance (<c>plugin:{pluginId}:{slug}:{action}</c>), so a new
/// instance starts with nobody holding them but those given here: the built-in Owner every action
/// on it, and the built-in Member those the plugin marks <see cref="PermissionDefinition.GrantToMembers"/>.
/// A default, not a rule: the tenant can take Member's away again, instance by instance.
/// </summary>
public static class PluginDefaultGrants
{
    /// <param name="memberDefaults">
    /// False where the roles come from elsewhere: an environment migration brings Member's
    /// permissions on the instance along, and must not restore what was taken away there.
    /// </param>
    public static async Task<int> ApplyAsync(
        TenancyDbContext db, ICacheService cache, Guid tenantId, PluginManifest manifest, string slug, bool memberDefaults, CancellationToken ct)
    {
        var roles = await db.TenantRoles.Include(r => r.Permissions)
            .Where(r => r.TenantId == tenantId && r.IsSystem
                        && (r.Name == TenantProvisioning.OwnerRole || r.Name == TenantProvisioning.MemberRoleName))
            .ToListAsync(ct);
        var added = 0;
        foreach (var role in roles)
        {
            var keys = manifest.Permissions
                .Where(p => role.Name == TenantProvisioning.OwnerRole || (memberDefaults && p.GrantToMembers))
                .Select(p => PlatformPermissions.ForPlugin(manifest.Id, slug, p.Action))
                .Except(role.Permissions.Select(p => p.Permission))
                .ToList();
            foreach (var key in keys)
            {
                db.TenantRolePermissions.Add(new TenantRolePermission
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    EnvironmentId = role.EnvironmentId,
                    TenantRoleId = role.Id,
                    Permission = key,
                });
            }
            added += keys.Count;
        }
        await db.SaveChangesAsync(ct);
        await PermissionCache.ForgetRolesAsync(db, cache, roles.Select(r => r.Id), ct);
        return added;
    }
}
