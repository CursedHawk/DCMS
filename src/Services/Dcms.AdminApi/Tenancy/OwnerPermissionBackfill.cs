using Dcms.Shared.Caching;
using Dcms.Shared.Data.Cms;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Dcms.Shared.Data.Rls;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Grants every platform and plugin permission to each tenant's system Owner role, for
/// permissions that did not exist when the role was created (a new platform key, a new plugin
/// instance, or a plugin gaining a permission).
///
/// <para>Plugin permissions are held per instance (<c>plugin:{pluginId}:{slug}:{action}</c>). A
/// role still holding a plugin-wide key (<c>plugin:{pluginId}:{action}</c>, from before) holds it
/// on every instance of that plugin in its environment instead: the same reach, in the new shape.</para>
///
/// <para>Owner means "everything" — <c>TenantProvisioning</c> seeds the role from
/// <see cref="PlatformPermissions.All"/> — but only at the moment the tenant is created. A
/// permission added later reaches new tenants and silently misses every existing one, so
/// <c>audit:read</c> would ship to an installation where nobody can open the audit page and
/// no error explains why. The failure looks like a bug in the feature rather than a gap in
/// its rollout, which is the worst kind.</para>
///
/// <para>Runs on every startup and is idempotent: it inserts only what is missing, and does
/// nothing at all once the roles are current. Deliberately additive — it never removes a
/// permission, because a role someone has narrowed by hand is a decision, not drift.</para>
/// </summary>
public static class OwnerPermissionBackfill
{
    /// <summary>
    /// What "everything" means for the Owner role: every platform permission, and every action
    /// of every plugin on each of the environment's <paramref name="instances"/>. The one
    /// definition both tenant provisioning and this backfill use, so they cannot disagree.
    /// </summary>
    public static IReadOnlyList<string> OwnerPermissions(IPluginCatalog catalog, IEnumerable<(string PluginId, string Slug)> instances) =>
        PlatformPermissions.All
            .Concat(instances.SelectMany(i => (catalog.Find(i.PluginId)?.Permissions ?? [])
                .Select(p => PlatformPermissions.ForPlugin(i.PluginId, i.Slug, p.Action))))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static async Task ApplyAsync(
        TenancyDbContext db,
        CmsDbContext cms,
        IPluginCatalog catalog,
        IAuditRecorder audit,
        ICacheService cache,
        ILogger logger,
        CancellationToken ct)
    {
        // Query filters are bypassed throughout: this runs at startup with no ambient tenant,
        // and the point is to reach every tenant's roles -- so platform scope for the
        // database too (ADR 0015).
        using var rls = RlsScope.Platform();
        var instances = (await cms.PluginInstances.IgnoreTenantFilter().AsNoTracking()
                .Select(p => new { p.TenantId, p.EnvironmentId, p.PluginId, p.Slug })
                .ToListAsync(ct))
            .ToLookup(i => (i.TenantId, i.EnvironmentId), i => (i.PluginId, i.Slug));

        var roles = await db.TenantRoles
            .IgnoreTenantFilter()
            .Where(r => (r.IsSystem && r.Name == TenantProvisioning.OwnerRole)
                        || r.Permissions.Any(p => p.Permission.StartsWith("plugin:") && !EF.Functions.Like(p.Permission, "plugin:%:%:%")))
            .Select(r => new
            {
                r.Id,
                r.TenantId,
                r.EnvironmentId,
                r.Name,
                Owner = r.IsSystem && r.Name == TenantProvisioning.OwnerRole,
                Held = r.Permissions.Select(p => new { p.Id, p.Permission }).ToList(),
            })
            .ToListAsync(ct);

        var (granted, scoped, changedRoles) = (0, 0, new List<Guid>());
        foreach (var role in roles)
        {
            var here = instances[(role.TenantId, role.EnvironmentId)].ToList();
            var wide = role.Held
                .Where(h => PluginPermissions.TryParse(h.Permission, out _, out var slug, out _) && slug is null)
                .ToList();
            var wanted = wide
                .SelectMany(w => here.Where(i => PluginPermissions.TryParse(w.Permission, out var pluginId, out _, out _) && i.PluginId == pluginId)
                    .Select(i => PluginPermissions.ForInstance(w.Permission, i.Slug)))
                .Concat(role.Owner ? OwnerPermissions(catalog, here) : [])
                .ToHashSet(StringComparer.Ordinal);
            var missing = wanted.Except(role.Held.Select(h => h.Permission), StringComparer.Ordinal).ToList();
            if (missing.Count == 0 && wide.Count == 0)
            {
                continue;
            }

            // Recorded before the save, so the grant and the record of it commit together —
            // and folded onto one entry per role, which is what an investigator wants to read:
            // "these keys were added to this role", not one row per key.
            audit.Record(AuditActions.RoleUpdated)
                .InTenant(role.TenantId)
                .For("role", role.Id, role.Name)
                .With("permissions_granted", missing)
                .With("permissions_removed", wide.Select(w => w.Permission).ToList())
                .With("reason", role.Owner && wide.Count == 0
                    ? "owner-role backfill: permissions introduced after the tenant was created"
                    : "plugin permissions are held per instance: plugin-wide keys became the same keys on each instance");

            foreach (var permission in missing)
            {
                db.TenantRolePermissions.Add(new TenantRolePermission
                {
                    Id = Guid.NewGuid(),
                    TenantId = role.TenantId,
                    EnvironmentId = role.EnvironmentId,
                    TenantRoleId = role.Id,
                    Permission = permission,
                });
            }
            await db.SaveChangesAsync(ct);
            if (wide.Count > 0)
            {
                var ids = wide.Select(w => w.Id).ToList();
                await db.TenantRolePermissions.IgnoreTenantFilter().Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync(ct);
            }
            granted += missing.Count;
            scoped += wide.Count;
            changedRoles.Add(role.Id);
        }

        // What members hold changed under the 5-minute permission cache.
        await PermissionCache.ForgetRolesAsync(db, cache, changedRoles, ct);
        if (granted > 0 || scoped > 0)
        {
            logger.LogInformation(
                "Granted {Count} permission(s) to roles and replaced {Scoped} plugin-wide key(s) with per-instance ones.", granted, scoped);
        }
    }
}
