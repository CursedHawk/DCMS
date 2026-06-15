using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

public static class TenancyEndpoints
{
    public static IEndpointRouteBuilder MapTenancyEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Cross-tenant: the caller's tenants (no ambient tenant needed) ----
        app.MapGet("/api/admin/me/tenants", async (CurrentUser me, TenancyDbContext db, CancellationToken ct) =>
        {
            var userId = me.RequireUserId();
            var memberships = await db.Memberships
                .IgnoreQueryFilters()
                .Where(m => m.UserId == userId)
                .Select(m => new { m.TenantId, RoleIds = m.Roles.Select(r => r.TenantRoleId) })
                .ToListAsync(ct);

            var tenantIds = memberships.Select(m => m.TenantId.ToString()).ToList();
            var tenants = await db.Tenants
                .Where(t => tenantIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.TenantId, ct);

            var result = memberships
                .Where(m => tenants.ContainsKey(m.TenantId))
                .Select(m => new
                {
                    tenantId = m.TenantId,
                    slug = tenants[m.TenantId].Identifier,
                    name = tenants[m.TenantId].Name,
                });
            return Results.Ok(result);
        }).RequireAuthorization();

        // ---- Tenant provisioning (platform SuperAdmin only) ----
        app.MapPost("/api/admin/tenants", async (
            CreateTenantRequest body, CurrentUser me, TenantProvisioning provisioning,
            TenancyDbContext db, CancellationToken ct) =>
        {
            if (!me.IsSuperAdmin)
            {
                return Results.Forbid();
            }
            if (string.IsNullOrWhiteSpace(body.Slug) || !IsSlug(body.Slug))
            {
                return Results.BadRequest(new { error = "slug must be kebab-case." });
            }
            if (await db.Tenants.AnyAsync(t => t.Identifier == body.Slug, ct))
            {
                return Results.Conflict(new { error = "slug already in use." });
            }

            // A platform admin may provision a tenant on behalf of an owner;
            // otherwise the caller becomes the owner.
            var ownerId = body.OwnerUserId ?? me.RequireUserId();
            var ownerEmail = body.OwnerEmail ?? me.Email ?? "unknown";
            var tenant = await provisioning.CreateTenantAsync(body.Slug, body.Name, ownerId, ownerEmail, ct);
            return Results.Created($"/api/admin/tenants/{tenant.Id}",
                new { tenantId = tenant.TenantId, slug = tenant.Identifier, name = tenant.Name });
        }).RequireAuthorization();

        app.MapGet("/api/admin/tenants", async (CurrentUser me, TenancyDbContext db, CancellationToken ct) =>
        {
            if (!me.IsSuperAdmin)
            {
                return Results.Forbid();
            }
            var tenants = await db.Tenants
                .Select(t => new { tenantId = t.TenantId, slug = t.Identifier, name = t.Name, status = t.Status.ToString() })
                .ToListAsync(ct);
            return Results.Ok(tenants);
        }).RequireAuthorization();

        // ---- Tenant-scoped: roles (requires X-Dcms-Tenant) ----
        app.MapGet("/api/admin/roles", async (TenancyDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (!tenant.HasTenant)
            {
                return Results.BadRequest(new { error = "Tenant header required." });
            }
            var roles = await db.TenantRoles
                .Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    isSystem = r.IsSystem,
                    permissions = r.Permissions.Select(p => p.Permission),
                })
                .ToListAsync(ct);
            return Results.Ok(roles);
        }).RequirePermission(PlatformPermissions.RolesManage);

        app.MapPost("/api/admin/roles", async (
            CreateRoleRequest body, TenancyDbContext db, ITenantContext tenant,
            IEventPublisher events, CancellationToken ct) =>
        {
            if (!tenant.HasTenant)
            {
                return Results.BadRequest(new { error = "Tenant header required." });
            }
            var tenantId = tenant.TenantId!.Value;
            var role = new TenantRole { Id = Guid.NewGuid(), TenantId = tenantId, Name = body.Name };
            foreach (var permission in body.Permissions.Distinct())
            {
                role.Permissions.Add(new TenantRolePermission
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    TenantRoleId = role.Id,
                    Permission = permission,
                });
            }
            db.TenantRoles.Add(role);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/roles/{role.Id}", new { id = role.Id });
        }).RequirePermission(PlatformPermissions.RolesManage);

        // ---- Tenant-scoped: members ----
        app.MapGet("/api/admin/members", async (TenancyDbContext db, CancellationToken ct) =>
        {
            var members = await db.Memberships
                .Select(m => new
                {
                    membershipId = m.Id,
                    userId = m.UserId,
                    email = m.Email,
                    roleIds = m.Roles.Select(r => r.TenantRoleId),
                })
                .ToListAsync(ct);
            return Results.Ok(members);
        }).RequirePermission(PlatformPermissions.MembersManage);

        app.MapPost("/api/admin/members/{membershipId:guid}/roles", async (
            Guid membershipId, AssignRoleRequest body, TenancyDbContext db, ITenantContext tenant,
            IEventPublisher events, TenancyPermissionResolver permissions, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var membership = await db.Memberships.Include(m => m.Roles)
                .FirstOrDefaultAsync(m => m.Id == membershipId, ct);
            if (membership is null)
            {
                return Results.NotFound();
            }
            if (!await db.TenantRoles.AnyAsync(r => r.Id == body.RoleId, ct))
            {
                return Results.BadRequest(new { error = "Unknown role." });
            }
            if (membership.Roles.All(r => r.TenantRoleId != body.RoleId))
            {
                db.MemberRoles.Add(new MemberRole
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    MembershipId = membershipId,
                    TenantRoleId = body.RoleId,
                });
                await db.SaveChangesAsync(ct);
                await permissions.InvalidateAsync(tenantId, membership.UserId, ct);
                await events.PublishAsync(Subjects.MembershipChanged,
                    new MembershipChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, membership.UserId), ct);
            }
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.MembersManage);

        return app;
    }

    private static bool IsSlug(string value) =>
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && !value.StartsWith('-') && !value.EndsWith('-');

    private sealed record CreateTenantRequest(string Slug, string? Name, Guid? OwnerUserId, string? OwnerEmail);
    private sealed record CreateRoleRequest(string Name, string[] Permissions);
    private sealed record AssignRoleRequest(Guid RoleId);
}
