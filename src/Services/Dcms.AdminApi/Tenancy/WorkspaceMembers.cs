using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// One member list per tenant, one set of roles per environment (ADR 0028).
///
/// <para>Memberships are the tenant's and every environment shares them; roles and who holds them
/// are each environment's, and a role there is what lets a member in once the tenant has more than
/// production (<see cref="TenantMembershipMiddleware"/>). Owner is the one role that is not an
/// environment's own: an environment's owners are exactly production's, mirrored here.</para>
///
/// <para>Driven by <c>membership.changed</c> (<see cref="MembershipChangedConsumer"/>), which every
/// membership write already publishes, so no path that changes one can forget this. Each pass is
/// idempotent and publishes only for what it actually changed, which ends the echo of its own events.</para>
/// </summary>
public sealed class WorkspaceMembers(
    TenancyDbContext db,
    TenancyPermissionResolver permissions,
    IEventPublisher events)
{
    public const string OwnerIsTheWorkspaces =
        "An environment's owners are the workspace's: grant or revoke Owner in the production environment.";

    /// <summary>The Owner role of an environment other than production, which only the mirror may grant.</summary>
    public static async Task<bool> IsMirroredOwnerRoleAsync(
        TenancyDbContext db, Dcms.Shared.Kernel.Abstractions.ITenantContext tenant, Guid roleId, CancellationToken ct) =>
        tenant.TenantId is { } tenantId && tenant.EnvironmentId is { } environmentId && environmentId != tenantId
        && await db.TenantRoles.AnyAsync(r => r.Id == roleId && r.IsSystem && r.Name == TenantProvisioning.OwnerRole, ct);

    /// <summary>Gives every other environment of the tenant the user's Owner in production, or takes it away.</summary>
    public async Task SyncOwnerAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var environments = (await TenancyPermissionResolver.EnvironmentIdsAsync(db, tenantId, ct)).Skip(1).ToList();
        if (environments.Count == 0) return;

        TenantMembership? membership;
        bool isOwner;
        using (RlsScope.Environment(tenantId, tenantId))
        {
            membership = await db.Memberships.IgnoreTenantFilter().AsNoTracking()
                .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId, ct);
            isOwner = membership is not null && await db.MemberRoles.IgnoreTenantFilter().AnyAsync(
                r => r.TenantId == tenantId && r.MembershipId == membership.Id
                     && r.Role!.IsSystem && r.Role.Name == TenantProvisioning.OwnerRole, ct);
        }
        // A departed member's roles went with the membership.
        if (membership is null) return;

        var changed = false;
        foreach (var environment in environments)
        {
            using var rls = RlsScope.Environment(tenantId, environment);
            var ownerRoleId = await db.TenantRoles.IgnoreTenantFilter()
                .Where(r => r.TenantId == tenantId && r.IsSystem && r.Name == TenantProvisioning.OwnerRole)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(ct);
            if (ownerRoleId is not { } roleId) continue;
            var held = await db.MemberRoles.IgnoreTenantFilter()
                .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.MembershipId == membership.Id && r.TenantRoleId == roleId, ct);
            if (isOwner && held is null)
            {
                db.MemberRoles.Add(new MemberRole { Id = Guid.NewGuid(), TenantId = tenantId, MembershipId = membership.Id, TenantRoleId = roleId });
            }
            else if (!isOwner && held is not null)
            {
                db.MemberRoles.Remove(held);
            }
            else continue;
            await db.SaveChangesAsync(ct);
            changed = true;
        }
        if (changed) await permissions.InvalidateAsync(tenantId, userId, ct);
    }

    /// <summary>
    /// A new environment's roles and who holds them: <paramref name="source"/>'s, role by role,
    /// where the environment has none of that name yet. Per-site repository grants are not
    /// copied, because they name the source's sites.
    /// </summary>
    public async Task SeedAsync(TenantEnvironment environment, TenantEnvironment source, CancellationToken ct)
    {
        var tenantId = environment.TenantId;
        List<TenantRole> roles;
        List<MemberRole> held;
        using (source.Scope())
        {
            roles = await db.TenantRoles.IgnoreTenantFilter().AsNoTracking()
                .Where(r => r.TenantId == tenantId)
                .Include(r => r.Permissions)
                .ToListAsync(ct);
            held = await db.MemberRoles.IgnoreTenantFilter().AsNoTracking()
                .Where(r => r.TenantId == tenantId)
                .ToListAsync(ct);
        }

        using var rls = environment.Scope();
        var targetRoles = await db.TenantRoles.IgnoreTenantFilter()
            .Where(r => r.TenantId == tenantId)
            .ToListAsync(ct);
        foreach (var role in roles.Where(r => targetRoles.All(t => t.Name != r.Name)))
        {
            var copy = new TenantRole { Id = Guid.NewGuid(), TenantId = tenantId, Name = role.Name, IsSystem = role.IsSystem };
            foreach (var permission in role.Permissions.Where(p => !p.Permission.StartsWith(Dcms.Shared.Security.PlatformPermissions.RepoPrefix, StringComparison.Ordinal)))
            {
                copy.Permissions.Add(new TenantRolePermission
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, TenantRoleId = copy.Id, Permission = permission.Permission,
                });
            }
            db.TenantRoles.Add(copy);
            targetRoles.Add(copy);
        }
        var roleByName = targetRoles.ToDictionary(r => r.Name, r => r.Id, StringComparer.Ordinal);
        var nameById = roles.ToDictionary(r => r.Id, r => r.Name);

        var existing = await db.MemberRoles.IgnoreTenantFilter()
            .Where(r => r.TenantId == tenantId)
            .Select(r => new { r.MembershipId, r.TenantRoleId })
            .ToListAsync(ct);
        foreach (var h in held)
        {
            if (!nameById.TryGetValue(h.TenantRoleId, out var name) || !roleByName.TryGetValue(name, out var roleId)) continue;
            if (existing.Any(e => e.MembershipId == h.MembershipId && e.TenantRoleId == roleId)) continue;
            db.MemberRoles.Add(new MemberRole { Id = Guid.NewGuid(), TenantId = tenantId, MembershipId = h.MembershipId, TenantRoleId = roleId });
        }
        await db.SaveChangesAsync(ct);

        var users = await db.Memberships.IgnoreTenantFilter().AsNoTracking()
            .Where(m => m.TenantId == tenantId && held.Select(h => h.MembershipId).Contains(m.Id))
            .Select(m => m.UserId)
            .ToListAsync(ct);
        foreach (var userId in users)
        {
            await permissions.InvalidateAsync(tenantId, userId, ct);
            await events.PublishAsync(Subjects.MembershipChanged,
                new MembershipChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, userId), ct);
        }
    }
}

public static class WorkspaceMembersEndpointExtensions
{
    /// <summary>
    /// Refuses the route in any environment but production: the member list is the whole
    /// workspace's (ADR 0028), and someone who manages members only in development must not add
    /// people to it or remove production's. Roles stay per environment, so only the routes that
    /// add or remove a member carry this.
    /// </summary>
    public static RouteHandlerBuilder InWorkspaceRootOnly(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var tenant = context.HttpContext.RequestServices.GetRequiredService<Dcms.Shared.Kernel.Abstractions.ITenantContext>();
            if (tenant.TenantId is { } tenantId && tenant.EnvironmentId is { } environmentId && environmentId != tenantId)
            {
                return Results.Conflict(new
                {
                    error = "Members are added and removed in the workspace's production environment; set their roles here.",
                });
            }
            return await next(context);
        });
}
