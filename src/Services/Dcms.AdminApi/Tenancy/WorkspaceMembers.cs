using Dcms.AdminApi.Sites.Git;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// One member list per workspace, one set of roles per environment (ADR 0026).
///
/// <para>The root holds the members: invitations, removals and departures happen there. Every
/// other environment mirrors each root membership as a bare one, and the roles granted in that
/// environment are what let the member in -- <see cref="TenantMembershipMiddleware"/> refuses a
/// role-less membership in a workspace with environments. Owner is the one role that is not the
/// environment's own: an environment's owners are exactly the root's.</para>
///
/// <para>Driven by <c>membership.changed</c> (<see cref="MembershipChangedConsumer"/>), which every
/// membership write already publishes, so no path that changes a membership can forget this. Each
/// pass is idempotent and publishes only for environments it actually changed, which is what ends
/// the echo of its own events.</para>
/// </summary>
public sealed class WorkspaceMembers(
    TenancyDbContext db,
    TenancyPermissionResolver permissions,
    IEventPublisher events,
    RepoAccessReconciler repoAccess)
{
    public const string OwnerIsTheWorkspaces =
        "An environment's owners are the workspace's: grant or revoke Owner in the production environment.";

    /// <summary>The Owner role of an environment other than the root, which only the mirror may grant.</summary>
    public static async Task<bool> IsMirroredOwnerRoleAsync(
        TenancyDbContext db, Dcms.Shared.Kernel.Abstractions.ITenantContext tenant, Guid roleId, CancellationToken ct)
    {
        if (!tenant.HasEnvironments || tenant.TenantId is not { } tenantId) return false;
        var isRoot = await db.Tenants.AsNoTracking()
            .AnyAsync(t => t.Id == tenantId.ToString() && t.WorkspaceId == tenantId, ct);
        return !isRoot && await db.TenantRoles.AnyAsync(
            r => r.Id == roleId && r.IsSystem && r.Name == TenantProvisioning.OwnerRole, ct);
    }

    public async Task SyncUserAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        // tenancy.tenants carries no policy.
        var workspaceId = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId.ToString())
            .Select(t => t.WorkspaceId)
            .FirstOrDefaultAsync(ct);
        if (workspaceId is not { } rootId) return;
        var environments = await db.Tenants.AsNoTracking()
            .Where(t => t.WorkspaceId == rootId && t.Id != rootId.ToString())
            .Select(t => t.Id)
            .ToListAsync(ct);
        if (environments.Count == 0) return;

        TenantMembership? source;
        bool isOwner;
        using (RlsScope.Tenant(rootId))
        {
            source = await db.Memberships.IgnoreTenantFilter().AsNoTracking()
                .FirstOrDefaultAsync(m => m.TenantId == rootId && m.UserId == userId, ct);
            isOwner = source is not null && await db.MemberRoles.IgnoreTenantFilter().AnyAsync(
                r => r.TenantId == rootId && r.MembershipId == source.Id
                     && r.Role!.IsSystem && r.Role.Name == TenantProvisioning.OwnerRole, ct);
        }

        foreach (var environment in environments.Select(Guid.Parse))
        {
            var email = await SyncIntoAsync(environment, userId, source, isOwner, ct);
            if (email is null) continue;
            await permissions.InvalidateAsync(environment, userId, ct);
            await events.PublishAsync(Subjects.MembershipChanged,
                new MembershipChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, environment, userId), ct);
            await repoAccess.ReconcileUserAsync(environment, userId, email, ct);
        }
    }

    /// <summary>
    /// A new environment's members: everyone in the workspace, holding the roles they hold in
    /// <paramref name="sourceId"/> where the environment has a role of that name. Custom roles are
    /// copied first so there is one to hold; per-site repository grants are not, because they name
    /// the source's sites.
    /// </summary>
    public async Task SeedAsync(Guid environmentId, Guid sourceId, CancellationToken ct)
    {
        List<TenantRole> roles;
        List<TenantMembership> members;
        using (RlsScope.Tenant(sourceId))
        {
            roles = await db.TenantRoles.IgnoreTenantFilter().AsNoTracking()
                .Where(r => r.TenantId == sourceId)
                .Include(r => r.Permissions)
                .ToListAsync(ct);
            members = await db.Memberships.IgnoreTenantFilter().AsNoTracking()
                .Where(m => m.TenantId == sourceId)
                .Include(m => m.Roles)
                .ToListAsync(ct);
        }

        using var rls = RlsScope.Tenant(environmentId);
        var targetRoles = await db.TenantRoles.IgnoreTenantFilter()
            .Where(r => r.TenantId == environmentId)
            .ToListAsync(ct);
        foreach (var role in roles.Where(r => targetRoles.All(t => t.Name != r.Name)))
        {
            var copy = new TenantRole { Id = Guid.NewGuid(), TenantId = environmentId, Name = role.Name, IsSystem = role.IsSystem };
            foreach (var permission in role.Permissions.Where(p => !p.Permission.StartsWith(Dcms.Shared.Security.PlatformPermissions.RepoPrefix, StringComparison.Ordinal)))
            {
                copy.Permissions.Add(new TenantRolePermission
                {
                    Id = Guid.NewGuid(), TenantId = environmentId, TenantRoleId = copy.Id, Permission = permission.Permission,
                });
            }
            db.TenantRoles.Add(copy);
            targetRoles.Add(copy);
        }
        var roleByName = targetRoles.ToDictionary(r => r.Name, r => r.Id, StringComparer.Ordinal);
        var nameById = roles.ToDictionary(r => r.Id, r => r.Name);

        var existing = await db.Memberships.IgnoreTenantFilter()
            .Where(m => m.TenantId == environmentId)
            .Include(m => m.Roles)
            .ToListAsync(ct);
        foreach (var member in members)
        {
            var target = existing.FirstOrDefault(m => m.UserId == member.UserId);
            if (target is null)
            {
                target = new TenantMembership { Id = Guid.NewGuid(), TenantId = environmentId, UserId = member.UserId, Email = member.Email };
                db.Memberships.Add(target);
            }
            foreach (var held in member.Roles)
            {
                if (!nameById.TryGetValue(held.TenantRoleId, out var name) || !roleByName.TryGetValue(name, out var roleId)) continue;
                if (target.Roles.Any(r => r.TenantRoleId == roleId)) continue;
                target.Roles.Add(new MemberRole { Id = Guid.NewGuid(), TenantId = environmentId, MembershipId = target.Id, TenantRoleId = roleId });
            }
        }
        await db.SaveChangesAsync(ct);

        foreach (var member in members)
        {
            await permissions.InvalidateAsync(environmentId, member.UserId, ct);
            await events.PublishAsync(Subjects.MembershipChanged,
                new MembershipChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, environmentId, member.UserId), ct);
        }
    }

    /// <summary>Brings one environment in line with the root; the member's email when it changed something.</summary>
    private async Task<string?> SyncIntoAsync(
        Guid environment, Guid userId, TenantMembership? source, bool isOwner, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(environment);
        var existing = await db.Memberships.IgnoreTenantFilter()
            .Include(m => m.Roles)
            .FirstOrDefaultAsync(m => m.TenantId == environment && m.UserId == userId, ct);

        if (source is null)
        {
            if (existing is null) return null;
            db.MemberRoles.RemoveRange(existing.Roles);
            db.Memberships.Remove(existing);
            await db.SaveChangesAsync(ct);
            return existing.Email;
        }

        var changed = false;
        if (existing is null)
        {
            existing = new TenantMembership { Id = Guid.NewGuid(), TenantId = environment, UserId = userId, Email = source.Email };
            db.Memberships.Add(existing);
            changed = true;
        }
        // Owner mirrors the root exactly, both ways: a root owner who is demoted or transferred
        // away stops owning every environment at the same moment. Owner cannot be granted or
        // revoked inside an environment for the same reason (TenancyEndpoints).
        var ownerRoleId = await db.TenantRoles.IgnoreTenantFilter()
            .Where(r => r.TenantId == environment && r.IsSystem && r.Name == TenantProvisioning.OwnerRole)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);
        if (ownerRoleId is { } roleId)
        {
            var held = existing.Roles.FirstOrDefault(r => r.TenantRoleId == roleId);
            if (isOwner && held is null)
            {
                existing.Roles.Add(new MemberRole { Id = Guid.NewGuid(), TenantId = environment, MembershipId = existing.Id, TenantRoleId = roleId });
                changed = true;
            }
            else if (!isOwner && held is not null)
            {
                db.MemberRoles.Remove(held);
                changed = true;
            }
        }
        if (!changed) return null;
        await db.SaveChangesAsync(ct);
        return existing.Email;
    }
}

public static class WorkspaceMembersEndpointExtensions
{
    /// <summary>
    /// Refuses the route in any environment but the workspace root: who belongs to the workspace
    /// is decided there and mirrored everywhere else (ADR 0026). Roles stay per environment, so
    /// only the routes that add or remove a member carry this.
    /// </summary>
    public static RouteHandlerBuilder InWorkspaceRootOnly(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var tenant = context.HttpContext.RequestServices.GetRequiredService<Dcms.Shared.Kernel.Abstractions.ITenantContext>();
            if (tenant is { HasEnvironments: true, TenantId: { } tenantId })
            {
                var db = context.HttpContext.RequestServices.GetRequiredService<TenancyDbContext>();
                var workspaceId = await db.Tenants.AsNoTracking()
                    .Where(t => t.Id == tenantId.ToString())
                    .Select(t => t.WorkspaceId)
                    .FirstOrDefaultAsync(context.HttpContext.RequestAborted);
                if (workspaceId != tenantId)
                {
                    return Results.Conflict(new
                    {
                        error = "Members are added and removed in the workspace's production environment; set their roles here.",
                    });
                }
            }
            return await next(context);
        });
}
