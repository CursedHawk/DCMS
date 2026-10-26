using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// A workspace's environments (ADR 0026). Each environment is a whole tenant; the root tenant is
/// the production environment, and <see cref="Tenant.WorkspaceId"/> is all that groups them.
///
/// <para>Managing them is the workspace's business, so <c>environments:manage</c> is checked in
/// the root whichever environment the caller is in -- a role that can manage environments in
/// development must not be able to add or delete them from there.</para>
/// </summary>
public static class EnvironmentEndpoints
{
    public static IEndpointRouteBuilder MapEnvironmentEndpoints(this IEndpointRouteBuilder app)
    {
        // Any member of any environment sees the whole workspace's list: it is how they switch.
        app.MapGet("/api/admin/environments", async (
            ITenantContext tenant, TenancyDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var current = await CurrentAsync(db, tenant, ct);
            if (current is null) return Results.BadRequest(new { error = "Select a workspace first." });
            var all = await WorkspaceAsync(db, current, ct);
            var userId = me.UserId;
            // rls: platform -- the caller's memberships across the workspace's tenants.
            using var rls = Dcms.Shared.Data.Rls.RlsScope.Platform();
            var ids = all.Select(t => t.TenantId).ToList();
            var memberOf = await db.Memberships.IgnoreQueryFilters()
                .Where(m => m.UserId == userId && ids.Contains(m.TenantId) && m.Roles.Any())
                .Select(m => m.TenantId)
                .ToListAsync(ct);
            return Results.Ok(new
            {
                enabled = current.WorkspaceId is not null,
                workspaceId = current.RootId,
                environments = all.Select(t => View(t, me.IsSuperAdmin || memberOf.Contains(t.TenantId))),
            });
        }).RequireAuthorization();

        app.MapPost("/api/admin/environments/enable", async (
            ITenantContext tenant, TenancyDbContext db, TenantStore store, CurrentUser me,
            IPermissionResolver permissions, IAuditRecorder audit, CancellationToken ct) =>
        {
            var root = await CurrentAsync(db, tenant, ct);
            if (root is null) return Results.BadRequest(new { error = "Select a workspace first." });
            if (!await CanManageAsync(root.RootId, me, permissions, ct)) return Results.Forbid();
            if (root.WorkspaceId is not null)
            {
                audit.Discard(audit.Declared!);
                return Results.NoContent();
            }
            root.WorkspaceId = root.TenantId;
            root.EnvironmentName = Tenant.ProductionEnvironment;
            root.EnvironmentOrder = 0;
            audit.Declared?.For("tenant", root.TenantId, root.Identifier);
            await db.SaveChangesAsync(ct);
            store.Evict(root);
            return Results.NoContent();
        }).RequireAuthorization().WithAudit(AuditActions.EnvironmentsEnabled, "tenant");

        // Only once the root is the workspace's last environment: turning environments off must
        // never orphan one.
        app.MapPost("/api/admin/environments/disable", async (
            ITenantContext tenant, TenancyDbContext db, TenantStore store, CurrentUser me,
            IPermissionResolver permissions, IAuditRecorder audit, CancellationToken ct) =>
        {
            var current = await CurrentAsync(db, tenant, ct);
            if (current is null) return Results.BadRequest(new { error = "Select a workspace first." });
            if (!await CanManageAsync(current.RootId, me, permissions, ct)) return Results.Forbid();
            var all = await WorkspaceAsync(db, current, ct);
            if (all.Count > 1)
            {
                return Results.Conflict(new { error = "Delete the other environments first." });
            }
            var root = all[0];
            if (root.WorkspaceId is null)
            {
                audit.Discard(audit.Declared!);
                return Results.NoContent();
            }
            root.WorkspaceId = null;
            root.EnvironmentName = null;
            root.EnvironmentOrder = 0;
            root.PromotesFrom.Clear();
            audit.Declared?.For("tenant", root.TenantId, root.Identifier);
            await db.SaveChangesAsync(ct);
            store.Evict(root);
            return Results.NoContent();
        }).RequireAuthorization().WithAudit(AuditActions.EnvironmentsDisabled, "tenant");

        // A new environment goes upstream of the current most-upstream one, which may then be
        // promoted into from it: adding staging, test and development in that order builds
        // development -> test -> staging -> production with no further setup.
        app.MapPost("/api/admin/environments", async (
            CreateEnvironmentRequest body, ITenantContext tenant, TenancyDbContext db, CurrentUser me,
            IPermissionResolver permissions, TenantProvisioning provisioning, WorkspaceMembers members,
            IAuditRecorder audit, CancellationToken ct) =>
        {
            var current = await CurrentAsync(db, tenant, ct);
            if (current is null) return Results.BadRequest(new { error = "Select a workspace first." });
            if (current.WorkspaceId is null) return Results.Conflict(new { error = "Enable environments first." });
            if (!await CanManageAsync(current.RootId, me, permissions, ct)) return Results.Forbid();

            var name = body.Name?.Trim().ToLowerInvariant() ?? "";
            if (!IsEnvironmentName(name))
            {
                return Results.BadRequest(new { error = "The name must be 1-32 lowercase letters, digits or dashes." });
            }
            var all = await WorkspaceAsync(db, current, ct);
            if (all.Count >= MaxEnvironments)
            {
                return Results.Conflict(new { error = $"A workspace can have at most {MaxEnvironments} environments." });
            }
            if (all.Any(t => t.EnvironmentName == name))
            {
                return Results.Conflict(new { error = "The workspace already has an environment of that name." });
            }
            var root = all.Single(t => t.TenantId == current.RootId);
            var slug = $"{root.Identifier}-{name}";
            if (slug.Length > 64)
            {
                return Results.BadRequest(new { error = "The workspace address and environment name are too long together." });
            }
            if (await db.Tenants.AnyAsync(t => t.Identifier == slug, ct))
            {
                return Results.Conflict(new { error = $"The address {slug} is already taken." });
            }

            var upstream = all.MinBy(t => t.EnvironmentOrder)!;
            // Before the provisioning save, which is what writes the record (stamped with the new
            // tenant, see TenantProvisioning).
            audit.Declared?.With("environment", name).With("workspace", root.Identifier);
            // Owned by the workspace's owner, not the caller. environments:manage can be delegated,
            // and a delegate who became Owner of every environment they created would hold there
            // what they were never given in the root. Everyone, the caller included, then gets the
            // roles they hold in the root (SeedAsync).
            var owner = await RootOwnerAsync(db, root.TenantId, ct);
            if (owner is null && !me.IsSuperAdmin)
            {
                return Results.Conflict(new { error = "The workspace has no owner to own the new environment." });
            }
            var created = await provisioning.CreateTenantAsync(
                slug, root.Name, owner?.UserId ?? me.RequireUserId(), owner?.Email ?? me.Email ?? "unknown", ct,
                t =>
                {
                    t.WorkspaceId = root.TenantId;
                    t.EnvironmentName = name;
                    t.EnvironmentOrder = upstream.EnvironmentOrder - 1;
                });
            upstream.PromotesFrom.Add(created.TenantId);
            await db.SaveChangesAsync(ct);
            await members.SeedAsync(created.TenantId, root.TenantId, ct);

            return Results.Created($"/api/admin/environments/{created.TenantId}", View(created, true));
        }).RequireAuthorization().WithAudit(AuditActions.EnvironmentCreated, "tenant");

        app.MapPatch("/api/admin/environments/{id:guid}", async (
            Guid id, UpdateEnvironmentRequest body, ITenantContext tenant, TenancyDbContext db, TenantStore store,
            CurrentUser me, IPermissionResolver permissions, IAuditRecorder audit, CancellationToken ct) =>
        {
            var current = await CurrentAsync(db, tenant, ct);
            if (current?.WorkspaceId is null) return Results.NotFound();
            if (!await CanManageAsync(current.RootId, me, permissions, ct)) return Results.Forbid();
            var all = await WorkspaceAsync(db, current, ct);
            var target = all.FirstOrDefault(t => t.TenantId == id);
            if (target is null) return Results.NotFound();

            // Renaming changes the label, never the slug: the slug names the Forgejo org and every
            // address and stored header, and is fixed for the environment's lifetime.
            if (body.Name is not null)
            {
                var name = body.Name.Trim().ToLowerInvariant();
                if (!IsEnvironmentName(name))
                {
                    return Results.BadRequest(new { error = "The name must be 1-32 lowercase letters, digits or dashes." });
                }
                if (all.Any(t => t.TenantId != id && t.EnvironmentName == name))
                {
                    return Results.Conflict(new { error = "The workspace already has an environment of that name." });
                }
                target.EnvironmentName = name;
            }
            if (body.Order is { } order) target.EnvironmentOrder = order;
            if (body.PromotesFrom is { } sources)
            {
                var ids = all.Select(t => t.TenantId).ToHashSet();
                if (sources.Any(s => s == id || !ids.Contains(s)))
                {
                    return Results.BadRequest(new { error = "An environment can only be promoted into from another environment of this workspace." });
                }
                target.PromotesFrom = sources.Distinct().ToList();
            }
            audit.Declared?.For("tenant", target.TenantId, target.Identifier)
                .With("name", target.EnvironmentName)
                .With("order", target.EnvironmentOrder)
                .With("promotes_from", target.PromotesFrom);
            await db.SaveChangesAsync(ct);
            store.Evict(target);
            return Results.Ok(View(target, true));
        }).RequireAuthorization().WithAudit(AuditActions.EnvironmentUpdated, "tenant");

        app.MapDelete("/api/admin/environments/{id:guid}", async (
            Guid id, ITenantContext tenant, TenancyDbContext db, CurrentUser me,
            IPermissionResolver permissions, TenantDeleter deleter, IAuditRecorder audit, CancellationToken ct) =>
        {
            var current = await CurrentAsync(db, tenant, ct);
            if (current?.WorkspaceId is null) return Results.NotFound();
            if (!await CanManageAsync(current.RootId, me, permissions, ct)) return Results.Forbid();
            if (id == current.RootId)
            {
                return Results.Conflict(new { error = "The production environment is the workspace; delete the workspace instead." });
            }
            var target = await db.Tenants.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id.ToString() && t.WorkspaceId == current.RootId, ct);
            if (target is null) return Results.NotFound();

            audit.Declared?.For("tenant", id, target.Identifier).With("environment", target.EnvironmentName);
            var result = await deleter.DeleteAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization().WithAudit(AuditActions.EnvironmentDeleted, "tenant");

        return app;
    }

    private static object View(Tenant t, bool isMember) => new
    {
        tenantId = t.TenantId,
        slug = t.Identifier,
        name = t.EnvironmentName ?? Tenant.ProductionEnvironment,
        order = t.EnvironmentOrder,
        isRoot = t.IsEnvironmentRoot,
        promotesFrom = t.PromotesFrom,
        status = t.Status.ToString(),
        isMember,
    };

    // tenancy.tenants is not tenant-scoped and carries no policy, so these read it directly.
    private static async Task<Tenant?> CurrentAsync(TenancyDbContext db, ITenantContext tenant, CancellationToken ct) =>
        tenant.TenantId is { } id
            ? await db.Tenants.FirstOrDefaultAsync(t => t.Id == id.ToString(), ct)
            : null;

    /// <summary>The workspace's environments, most upstream first; just the tenant while they are off.</summary>
    private static async Task<List<Tenant>> WorkspaceAsync(TenancyDbContext db, Tenant current, CancellationToken ct)
    {
        if (current.WorkspaceId is not { } workspaceId) return [current];
        return await db.Tenants
            .Where(t => t.WorkspaceId == workspaceId)
            .OrderBy(t => t.EnvironmentOrder)
            .ToListAsync(ct);
    }

    private static async Task<bool> CanManageAsync(
        Guid rootId, CurrentUser me, IPermissionResolver permissions, CancellationToken ct) =>
        me.IsSuperAdmin
        || (me.UserId is { } userId
            && (await permissions.GetPermissionsAsync(rootId, userId, ct)).Contains(PlatformPermissions.EnvironmentsManage));

    // ponytail: a fixed cap; per-plan limits if environments are ever sold by the count.
    private const int MaxEnvironments = 10;

    private static async Task<TenantMembership?> RootOwnerAsync(TenancyDbContext db, Guid rootId, CancellationToken ct)
    {
        using var rls = Dcms.Shared.Data.Rls.RlsScope.Tenant(rootId);
        return await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == rootId
                        && m.Roles.Any(r => r.Role!.IsSystem && r.Role.Name == TenantProvisioning.OwnerRole))
            .OrderBy(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private static bool IsEnvironmentName(string value) =>
        value.Length is > 0 and <= 32
        && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && !value.StartsWith('-') && !value.EndsWith('-');

    private sealed record CreateEnvironmentRequest(string? Name);
    private sealed record UpdateEnvironmentRequest(string? Name, int? Order, List<Guid>? PromotesFrom);
}
