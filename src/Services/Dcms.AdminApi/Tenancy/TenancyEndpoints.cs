using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
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

        // The caller's effective permissions in the selected tenant — drives the
        // SPA's permission-filtered navigation and action gating. SuperAdmins see
        // the full platform set; with no tenant selected the set is empty.
        app.MapGet("/api/admin/me/permissions", async (
            CurrentUser me, ITenantContext tenant, IPermissionResolver resolver, CancellationToken ct) =>
        {
            var userId = me.RequireUserId();
            if (me.IsSuperAdmin)
            {
                return Results.Ok(new { isSuperAdmin = true, permissions = PlatformPermissions.All });
            }
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.Ok(new { isSuperAdmin = false, permissions = Array.Empty<string>() });
            }
            var permissions = await resolver.GetPermissionsAsync(tenantId, userId, ct);
            return Results.Ok(new { isSuperAdmin = false, permissions });
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

        // Update a role's name and replace its permission set wholesale. System
        // roles keep their name fixed but their permissions may still be tuned.
        app.MapPut("/api/admin/roles/{id:guid}", async (
            Guid id, UpdateRoleRequest body, TenancyDbContext db, ITenantContext tenant,
            CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var role = await db.TenantRoles.Include(r => r.Permissions)
                .FirstOrDefaultAsync(r => r.Id == id, ct);
            if (role is null)
            {
                return Results.NotFound();
            }
            if (!role.IsSystem && !string.IsNullOrWhiteSpace(body.Name))
            {
                role.Name = body.Name;
            }
            db.TenantRolePermissions.RemoveRange(role.Permissions);
            role.Permissions.Clear();
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
            await db.SaveChangesAsync(ct);
            // Members holding this role get fresh permissions on next resolve (5-min
            // TTL); role edits are infrequent so we let the cache lapse naturally.
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.RolesManage);

        app.MapDelete("/api/admin/roles/{id:guid}", async (
            Guid id, TenancyDbContext db, CancellationToken ct) =>
        {
            var role = await db.TenantRoles.Include(r => r.Permissions)
                .FirstOrDefaultAsync(r => r.Id == id, ct);
            if (role is null)
            {
                return Results.NotFound();
            }
            if (role.IsSystem)
            {
                return Results.BadRequest(new { error = "System roles cannot be deleted." });
            }
            var assignments = await db.MemberRoles.Where(m => m.TenantRoleId == id).ToListAsync(ct);
            db.MemberRoles.RemoveRange(assignments);
            db.TenantRolePermissions.RemoveRange(role.Permissions);
            db.TenantRoles.Remove(role);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
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

        app.MapDelete("/api/admin/members/{membershipId:guid}/roles/{roleId:guid}", async (
            Guid membershipId, Guid roleId, TenancyDbContext db, ITenantContext tenant,
            IEventPublisher events, TenancyPermissionResolver permissions, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var membership = await db.Memberships.Include(m => m.Roles)
                .FirstOrDefaultAsync(m => m.Id == membershipId, ct);
            if (membership is null)
            {
                return Results.NotFound();
            }
            var assignment = membership.Roles.FirstOrDefault(r => r.TenantRoleId == roleId);
            if (assignment is not null)
            {
                db.MemberRoles.Remove(assignment);
                await db.SaveChangesAsync(ct);
                await permissions.InvalidateAsync(tenantId, membership.UserId, ct);
                await events.PublishAsync(Subjects.MembershipChanged,
                    new MembershipChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, membership.UserId), ct);
            }
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.MembersManage);

        // Effective permission catalog: platform keys ∪ installed plugins' manifest
        // permissions, each with a display name and group — drives the role matrix.
        app.MapGet("/api/admin/permissions/catalog", (IPluginCatalog catalog) =>
        {
            var platform = PlatformPermissions.All.Select(k => new
            {
                key = k,
                displayName = PlatformPermissionName(k),
                group = "Platform",
            });
            var plugin = catalog.Manifests.SelectMany(m => m.Permissions.Select(p => new
            {
                key = PlatformPermissions.ForPlugin(m.Id, p.Action),
                displayName = p.DisplayName,
                group = m.Name,
            }));
            return Results.Ok(platform.Concat(plugin));
        }).RequireAuthorization();

        return app;
    }

    private static string PlatformPermissionName(string key) => key switch
    {
        PlatformPermissions.TenantSettings => "Manage tenant settings",
        PlatformPermissions.MembersManage => "Manage members",
        PlatformPermissions.RolesManage => "Manage roles",
        PlatformPermissions.DomainsManage => "Manage domains",
        PlatformPermissions.PluginsManage => "Manage plugins",
        PlatformPermissions.MediaRead => "View media",
        PlatformPermissions.MediaWrite => "Upload media",
        PlatformPermissions.SiteEdit => "Edit sites",
        PlatformPermissions.SitePublish => "Publish sites",
        PlatformPermissions.AiSettings => "Manage AI settings",
        PlatformPermissions.AnalyticsRead => "View analytics",
        PlatformPermissions.ContentRead => "View content",
        PlatformPermissions.ContentWrite => "Edit content",
        PlatformPermissions.ContentPublish => "Publish content",
        PlatformPermissions.ChatRead => "View chat",
        PlatformPermissions.ChatManage => "Manage chat",
        _ => key,
    };

    private static bool IsSlug(string value) =>
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && !value.StartsWith('-') && !value.EndsWith('-');

    private sealed record CreateTenantRequest(string Slug, string? Name, Guid? OwnerUserId, string? OwnerEmail);
    private sealed record CreateRoleRequest(string Name, string[] Permissions);
    private sealed record UpdateRoleRequest(string? Name, string[] Permissions);
    private sealed record AssignRoleRequest(Guid RoleId);
}
