using System.Text.Json;
using Dcms.AdminApi.Tenancy;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Environments.Kinds;

/// <summary>
/// Roles and what they allow, by name (ADR 0026). Who holds them is each environment's own — that
/// is the point of a role per environment — and so are per-site repository grants, which name the
/// environment's own sites.
/// </summary>
// rls: EnvironmentMigrator calls every kind inside RlsScope.Tenant(tenantId), the tenant each
// query names; IgnoreQueryFilters only drops the request's own, which is another environment.
public sealed class RolesConfigKind(TenancyDbContext db, TenancyPermissionResolver permissions) : IEnvironmentConfigKind
{
    public string Kind => "roles";

    public string? Permission => PlatformPermissions.RolesManage;

    public int Order => 10;

    private sealed record Value(bool System, string[] Permissions);

    public async Task<IReadOnlyList<EnvironmentConfigItem>> ExportAsync(Guid tenantId, CancellationToken ct)
    {
        var roles = await db.TenantRoles.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .Include(r => r.Permissions)
            .ToListAsync(ct);
        // Owner is every permission in every environment (OwnerPermissionBackfill), and the role
        // a deployment must never be able to narrow or forge.
        return roles.Where(r => !IsOwner(r)).Select(r => new EnvironmentConfigItem(r.Name, JsonSerializer.SerializeToElement(new Value(
                r.IsSystem,
                r.Permissions.Select(p => p.Permission).Where(Moves).Distinct().Order(StringComparer.Ordinal).ToArray()))))
            .ToList();
    }

    public async Task ApplyAsync(Guid tenantId, string key, JsonElement? value, EnvironmentApplyContext context, CancellationToken ct)
    {
        if (key == TenantProvisioning.OwnerRole) throw new EnvironmentMigrationException("environments.errors.notAllowed", key);
        var role = await db.TenantRoles.IgnoreQueryFilters()
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == key, ct);
        if (value is null)
        {
            // A system role is every environment's; only a custom one goes.
            if (role is null || role.IsSystem) return;
            db.MemberRoles.RemoveRange(await db.MemberRoles.IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && m.TenantRoleId == role.Id).ToListAsync(ct));
            db.TenantRolePermissions.RemoveRange(role.Permissions);
            db.TenantRoles.Remove(role);
        }
        else
        {
            var wanted = value.Value.Deserialize<Value>()!;
            if (role is null)
            {
                // Never system: a system role is one provisioning made, not one a migration names.
                role = new TenantRole { Id = Guid.NewGuid(), TenantId = tenantId, Name = key };
                db.TenantRoles.Add(role);
            }
            var keep = role.Permissions.Where(p => !Moves(p.Permission) || wanted.Permissions.Contains(p.Permission)).ToList();
            db.TenantRolePermissions.RemoveRange(role.Permissions.Except(keep));
            foreach (var permission in wanted.Permissions.Where(p => keep.All(k => k.Permission != p)))
            {
                db.TenantRolePermissions.Add(new TenantRolePermission
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, TenantRoleId = role.Id, Permission = permission,
                });
            }
        }
        await db.SaveChangesAsync(ct);

        // Whoever holds it now holds something else.
        var users = await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenantId).Select(m => m.UserId).ToListAsync(ct);
        foreach (var user in users) await permissions.InvalidateAsync(tenantId, user, ct);
    }

    /// <summary>The permissions <paramref name="value"/> grants that <paramref name="current"/> does not: what the applier must be able to grant.</summary>
    public static IEnumerable<string> Added(JsonElement? current, JsonElement? value)
    {
        if (value is null) return [];
        var had = current?.Deserialize<Value>()?.Permissions ?? [];
        return value.Value.Deserialize<Value>()!.Permissions.Except(had);
    }

    private static bool IsOwner(TenantRole r) => r.IsSystem && r.Name == TenantProvisioning.OwnerRole;

    private static bool Moves(string permission) =>
        !permission.StartsWith(PlatformPermissions.RepoPrefix, StringComparison.Ordinal);
}
