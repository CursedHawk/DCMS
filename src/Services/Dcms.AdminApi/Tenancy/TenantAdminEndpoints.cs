using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.AdminApi.Sites;
using Dcms.AdminApi.Sites.Git;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Data.Analytics;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.DynamicApps;
using Dcms.Shared.Data.Functions;
using Dcms.Shared.Data.UserAuth;
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Data.Search;
using Dcms.Shared.Data.Social;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Realms;
using Dcms.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Dcms.Shared.Data.Rls;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Owner-level operations on the currently selected tenant: view/rename it,
/// hand it to someone else, or delete it outright.
///
/// These act on the ambient tenant (the X-Dcms-Tenant header) rather than an id in
/// the path, like the rest of the tenant-scoped API — it removes any chance of the
/// path and the header disagreeing about which tenant is being destroyed.
/// </summary>
public static class TenantAdminEndpoints
{
    public static IEndpointRouteBuilder MapTenantAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // What the workspace-settings page shows, including the counts the delete
        // confirmation lists — an owner should see the size of what they are about
        // to destroy before they type the name.
        app.MapGet("/api/admin/tenant", async (
            TenancyDbContext db, SitesDbContext sites, MediaDbContext media, CmsDbContext cms,
            ITenantContext tenant, CurrentUser me, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }
            var key = tenantId.ToString();
            var row = await db.Tenants.FirstOrDefaultAsync(t => t.Id == key, ct);
            if (row is null)
            {
                return Results.NotFound();
            }

            var owners = await OwnerMembershipsAsync(db, ct);
            var members = await db.Memberships
                .OrderBy(m => m.Email)
                .Select(m => new { membershipId = m.Id, userId = m.UserId, email = m.Email })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                tenantId,
                slug = row.Identifier,
                name = row.Name,
                status = row.Status.ToString(),
                createdAt = row.CreatedAt,
                owners = owners.Select(o => new { o.UserId, o.Email }),
                isOwner = owners.Any(o => o.UserId == me.UserId) || me.IsSuperAdmin,
                members,
                counts = new
                {
                    members = members.Count,
                    sites = await sites.Sites.CountAsync(ct),
                    domains = await db.Domains.CountAsync(ct),
                    mediaAssets = await media.Assets.CountAsync(ct),
                    contentItems = await cms.ContentItems.CountAsync(ct),
                },
            });
        }).RequirePermission(PlatformPermissions.TenantSettings);

        app.MapPatch("/api/admin/tenant", async (
            RenameTenantRequest body, TenancyDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }
            var name = body.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest(new { error = "Name required." });
            }
            var key = tenantId.ToString();
            var row = await db.Tenants.FirstOrDefaultAsync(t => t.Id == key, ct);
            if (row is null)
            {
                return Results.NotFound();
            }
            // The slug is deliberately not editable: it is the tenant header, the
            // Forgejo org name and part of every stored object key.
            row.Name = name;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { name = row.Name });
        }).RequirePermission(PlatformPermissions.TenantSettings).WithAudit(AuditActions.TenantUpdated, "tenant");

        // Hand the tenant to another member. A transfer, not a co-ownership grant:
        // the target gains the Owner role and every other member loses it, so
        // afterwards there is exactly one owner and no ambiguity about who that is.
        app.MapPost("/api/admin/tenant/transfer", async (
            TransferTenantRequest body, TenancyDbContext db, ITenantContext tenant, CurrentUser me,
            TenancyPermissionResolver permissions, IEventPublisher events,
            RepoAccessReconciler repoAccess, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }
            if (!await IsOwnerAsync(db, me, ct))
            {
                return Results.Forbid();
            }

            var ownerRole = await db.TenantRoles
                .FirstOrDefaultAsync(r => r.IsSystem && r.Name == TenantProvisioning.OwnerRole, ct);
            if (ownerRole is null)
            {
                return Results.Problem("This tenant has no Owner role.", statusCode: 500);
            }

            var target = await db.Memberships.Include(m => m.Roles)
                .FirstOrDefaultAsync(m => m.Id == body.MembershipId, ct);
            if (target is null)
            {
                return Results.BadRequest(new { error = "That member does not belong to this tenant." });
            }

            var current = await db.MemberRoles
                .Where(mr => mr.TenantRoleId == ownerRole.Id && mr.MembershipId != target.Id)
                .ToListAsync(ct);
            db.MemberRoles.RemoveRange(current);

            if (target.Roles.All(r => r.TenantRoleId != ownerRole.Id))
            {
                db.MemberRoles.Add(new MemberRole
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    MembershipId = target.Id,
                    TenantRoleId = ownerRole.Id,
                });
            }
            await db.SaveChangesAsync(ct);

            // Everyone whose role set moved needs their cached permissions dropped and
            // their git repo access re-derived — the outgoing owner most of all, since
            // they may have just lost push rights to every repo.
            var affected = await db.Memberships
                .Where(m => m.Id == target.Id || current.Select(c => c.MembershipId).Contains(m.Id))
                .Select(m => new { m.Id, m.UserId, m.Email })
                .ToListAsync(ct);
            foreach (var member in affected)
            {
                await permissions.InvalidateAsync(tenantId, member.UserId, ct);
                await events.PublishAsync(Subjects.MembershipChanged,
                    new MembershipChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, member.UserId), ct);
                await repoAccess.ReconcileUserAsync(tenantId, member.UserId, member.Email, ct);
            }

            return Results.Ok(new { ownerMembershipId = target.Id, ownerEmail = target.Email });
        }).RequirePermission(PlatformPermissions.TenantSettings).WithAudit(AuditActions.TenantTransferred, "tenant");

        app.MapDelete("/api/admin/tenant", async (
            TenancyDbContext db, CurrentUser me, ITenantContext tenant,
            TenantDeleter deleter, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }
            if (!await IsOwnerAsync(db, me, ct))
            {
                return Results.Forbid();
            }
            var result = await deleter.DeleteAsync(tenantId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequirePermission(PlatformPermissions.TenantSettings).WithAudit(AuditActions.TenantPurged, "tenant");

        return app;
    }

    private sealed record OwnerRow(Guid UserId, string Email);

    private static async Task<List<OwnerRow>> OwnerMembershipsAsync(TenancyDbContext db, CancellationToken ct) =>
        await db.Memberships
            .Where(m => m.Roles.Any(r => r.Role!.IsSystem && r.Role.Name == TenantProvisioning.OwnerRole))
            .Select(m => new OwnerRow(m.UserId, m.Email))
            .ToListAsync(ct);

    /// <summary>
    /// Ownership acts (transfer, delete) need more than the tenant:settings
    /// permission that opens the settings page — a role could hand that out freely.
    /// The platform SuperAdmin is always allowed, so a tenant whose owner has left
    /// is not stuck.
    /// </summary>
    private static async Task<bool> IsOwnerAsync(TenancyDbContext db, CurrentUser me, CancellationToken ct)
    {
        if (me.IsSuperAdmin)
        {
            return true;
        }
        var userId = me.UserId;
        if (userId is null)
        {
            return false;
        }
        return await db.Memberships.AnyAsync(
            m => m.UserId == userId && m.Roles.Any(r => r.Role!.IsSystem && r.Role.Name == TenantProvisioning.OwnerRole),
            ct);
    }

    private sealed record RenameTenantRequest(string? Name);
    private sealed record TransferTenantRequest(Guid MembershipId);
}
