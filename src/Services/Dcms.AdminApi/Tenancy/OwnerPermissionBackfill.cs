using Dcms.Shared.Audit;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Grants every platform permission to each tenant's system Owner role, for permissions that
/// did not exist when the role was created.
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
    public static async Task ApplyAsync(
        TenancyDbContext db,
        IAuditRecorder audit,
        ILogger logger,
        CancellationToken ct)
    {
        // Query filters are bypassed throughout: this runs at startup with no ambient tenant,
        // and the point is to reach every tenant's Owner role.
        var ownerRoles = await db.TenantRoles
            .IgnoreQueryFilters()
            .Where(r => r.IsSystem && r.Name == TenantProvisioning.OwnerRole)
            .Select(r => new
            {
                r.Id,
                r.TenantId,
                Held = r.Permissions.Select(p => p.Permission).ToList(),
            })
            .ToListAsync(ct);

        var granted = 0;

        foreach (var role in ownerRoles)
        {
            var missing = PlatformPermissions.All
                .Except(role.Held, StringComparer.Ordinal)
                .ToList();

            if (missing.Count == 0)
            {
                continue;
            }

            // Recorded before the save, so the grant and the record of it commit together —
            // and folded onto one entry per role, which is what an investigator wants to read:
            // "these keys were added to this role", not one row per key.
            audit.Record(AuditActions.RoleUpdated)
                .InTenant(role.TenantId)
                .For("role", role.Id, TenantProvisioning.OwnerRole)
                .With("permissions_granted", missing)
                .With("reason", "owner-role backfill: permissions introduced after the tenant was created");

            foreach (var permission in missing)
            {
                db.TenantRolePermissions.Add(new TenantRolePermission
                {
                    Id = Guid.NewGuid(),
                    TenantId = role.TenantId,
                    TenantRoleId = role.Id,
                    Permission = permission,
                });
            }

            await db.SaveChangesAsync(ct);
            granted += missing.Count;
        }

        if (granted > 0)
        {
            logger.LogInformation(
                "Backfilled {Count} permission(s) onto {Roles} Owner role(s).", granted, ownerRoles.Count);
        }
    }
}
