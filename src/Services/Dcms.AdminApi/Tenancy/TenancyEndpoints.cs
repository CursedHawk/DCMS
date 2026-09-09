using Dcms.AdminApi.Notifications;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Dcms.Shared.Telemetry;
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
        }).RequireAuthorization().AllowNonMemberTenant(AllowNonMemberTenantAttribute.SelfScoped);

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
        }).RequireAuthorization().AllowNonMemberTenant(AllowNonMemberTenantAttribute.SelfScoped);

        // ---- Tenant provisioning (platform SuperAdmin only) ----
        app.MapPost("/api/admin/tenants", async (
            CreateTenantRequest body, CurrentUser me, TenantProvisioning provisioning,
            TenancyDbContext db, DcmsMetrics metrics, CancellationToken ct) =>
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

            // Unlabelled on purpose. The tenant that was created is exactly the value that must
            // not become a metric label — one series per tenant, forever — and the growth
            // dashboard wants the rate, which the bare count gives. Which tenants exist is a
            // question for obs.v_growth_daily.
            metrics.TenantCreated();
            return Results.Created($"/api/admin/tenants/{tenant.Id}",
                new { tenantId = tenant.TenantId, slug = tenant.Identifier, name = tenant.Name });
        }).RequireAuthorization().WithAudit(AuditActions.TenantCreated, "tenant");

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

        // ---- Cross-tenant lifecycle: suspend / resume (SuperAdmin, no ambient tenant) ----
        //
        // TenantStatus.Suspended was declared when the tenancy model was written and then read
        // by nothing at all: no endpoint set it and no code branched on it, so a "suspended"
        // tenant behaved exactly like an active one. These two endpoints and the two
        // enforcement points they rely on (TenantMembershipMiddleware for the admin plane,
        // site-host's DomainResolver for the delivery plane) are what make the status mean
        // something.
        //
        // The tenant is named in the ROUTE rather than the X-Dcms-Tenant header on purpose: an
        // operator suspending a tenant is not working inside it, and requiring the header would
        // mean the console had to switch context to a tenant it is about to shut off.
        app.MapPost("/api/admin/tenants/{tenantId:guid}/suspend", async (
            Guid tenantId, ConsoleCaller console, TenancyDbContext db, IAuditRecorder audit,
            IEventPublisher events, ILoggerFactory loggerFactory, CancellationToken ct) =>
                await SetTenantStatusAsync(
                    tenantId, TenantStatus.Suspended, console, db, audit, events,
                    loggerFactory.CreateLogger("TenantLifecycle"), ct))
            .RequireAuthorization()
            .WithAudit(AuditActions.PlatformTenantSuspended, "tenant")
            .AllowConsoleService(
                "the platform console owns tenant lifecycle; its API holds dcms.console and has "
                + "already checked the operator holds platform:tenants:lifecycle. ADR 0003 keeps "
                + "the write here, so the console asks rather than reaching into tenancy itself.");

        app.MapPost("/api/admin/tenants/{tenantId:guid}/resume", async (
            Guid tenantId, ConsoleCaller console, TenancyDbContext db, IAuditRecorder audit,
            IEventPublisher events, ILoggerFactory loggerFactory, CancellationToken ct) =>
                await SetTenantStatusAsync(
                    tenantId, TenantStatus.Active, console, db, audit, events,
                    loggerFactory.CreateLogger("TenantLifecycle"), ct))
            .RequireAuthorization()
            .WithAudit(AuditActions.PlatformTenantResumed, "tenant")
            .AllowConsoleService(
                "the platform console owns tenant lifecycle; its API holds dcms.console and has "
                + "already checked the operator holds platform:tenants:lifecycle. ADR 0003 keeps "
                + "the write here, so the console asks rather than reaching into tenancy itself.");

        // ---- Tenant-scoped: roles (requires X-Dcms-Tenant) ----
        app.MapGet("/api/admin/roles", async (TenancyDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (!tenant.HasTenant)
            {
                return Results.BadRequest(new { error = "Tenant header required." });
            }
            var roles = await db.TenantRoles
                .OrderBy(r => r.Name)
                .Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    isSystem = r.IsSystem,
                    permissions = r.Permissions.Select(p => p.Permission),
                    // Who this role currently affects. Editing a role that nobody holds
                    // is free; editing one held by fifteen people is not, and the list
                    // should say which it is before the dialog opens.
                    memberCount = r.Members.Count,
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
        }).RequirePermission(PlatformPermissions.RolesManage).WithAudit(AuditActions.RoleCreated, "role");

        // Update a role's name and replace its permission set wholesale. System
        // roles keep their name fixed but their permissions may still be tuned.
        app.MapPut("/api/admin/roles/{id:guid}", async (
            Guid id, UpdateRoleRequest body, TenancyDbContext db, ITenantContext tenant,
            TenancyPermissionResolver permissions, Dcms.AdminApi.Sites.Git.RepoAccessReconciler repoAccess,
            IAuditRecorder audit, AuditScope scope, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var role = await db.TenantRoles.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (role is null)
            {
                return Results.NotFound();
            }
            if (!role.IsSystem && !string.IsNullOrWhiteSpace(body.Name))
            {
                role.Name = body.Name;
            }

            // Replace the permission set wholesale. We hard-delete the existing rows
            // with ExecuteDelete rather than RemoveRange-ing the tracked navigation and
            // re-adding: removing then re-adding dependents of a required relationship in
            // one SaveChanges makes EF's change-tracker fixup emit UPDATEs keyed on the
            // brand-new rows' Ids (which don't exist yet) → "affected 0 rows"
            // DbUpdateConcurrencyException. A direct delete + plain inserts sidesteps the
            // tracker entirely. Wrapped in a transaction so the swap is atomic. The
            // ExecuteDelete query honours the tenant query filter, so it is tenant-scoped.
            //
            // Read the old set before deleting it. A set-based delete leaves no before-image,
            // and "who changed this role's permissions, and to what" is the question this
            // whole subsystem exists to answer — a permission set is tens of short strings, so
            // loading it to be able to answer is not a cost worth optimising away.
            using var _ = scope.SuppressBulkCapture();
            var previous = await db.TenantRolePermissions
                .Where(p => p.TenantRoleId == id).Select(p => p.Permission).OrderBy(p => p).ToListAsync(ct);
            var next = body.Permissions.Distinct().Order(StringComparer.Ordinal).ToList();

            audit.Declared?
                .For("role", role.Id, role.Name)
                .Changed("permissions", string.Join(" ", previous), string.Join(" ", next))
                .With("permissions_granted", next.Except(previous, StringComparer.Ordinal).ToList())
                .With("permissions_revoked", previous.Except(next, StringComparer.Ordinal).ToList());

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.TenantRolePermissions.Where(p => p.TenantRoleId == id).ExecuteDeleteAsync(ct);
            foreach (var permission in next)
            {
                db.TenantRolePermissions.Add(new TenantRolePermission
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    TenantRoleId = role.Id,
                    Permission = permission,
                });
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Members holding this role: invalidate their cached permissions and
            // reconcile Forgejo repo access so changed repo:{site} grants take effect
            // immediately (not after the 5-min TTL).
            var affected = await db.Memberships
                .Where(m => m.Roles.Any(r => r.TenantRoleId == id))
                .Select(m => new { m.UserId, m.Email })
                .ToListAsync(ct);
            foreach (var m in affected)
            {
                await permissions.InvalidateAsync(tenantId, m.UserId, ct);
                await repoAccess.ReconcileUserAsync(tenantId, m.UserId, m.Email, ct);
            }
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.RolesManage).WithAudit(AuditActions.RoleUpdated, "role");

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
        }).RequirePermission(PlatformPermissions.RolesManage).WithAudit(AuditActions.RoleDeleted, "role");

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
            IEventPublisher events, TenancyPermissionResolver permissions, CurrentUser me,
            INotificationPublisher notifications,
            Dcms.AdminApi.Sites.Git.RepoAccessReconciler repoAccess, CancellationToken ct) =>
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
                // Sync the user's Forgejo repo access to their new permission set.
                await repoAccess.ReconcileUserAsync(tenantId, membership.UserId, membership.Email, ct);

                // Raised here rather than off membership.changed: that event carries only
                // (tenant, user) and fires identically for a grant, a revoke and an
                // invitation acceptance, so a consumer cannot tell them apart or name the role.
                await notifications.RaiseAsync(RoleChangeNotification(
                    tenantId, membership, body.RoleId, granted: true, actor: me.UserId), ct);
            }
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.MembersManage).WithAudit(AuditActions.MemberRoleGranted, "membership");

        app.MapDelete("/api/admin/members/{membershipId:guid}/roles/{roleId:guid}", async (
            Guid membershipId, Guid roleId, TenancyDbContext db, ITenantContext tenant,
            IEventPublisher events, TenancyPermissionResolver permissions, CurrentUser me,
            INotificationPublisher notifications,
            Dcms.AdminApi.Sites.Git.RepoAccessReconciler repoAccess, CancellationToken ct) =>
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
                // Sync the user's Forgejo repo access to their reduced permission set.
                await repoAccess.ReconcileUserAsync(tenantId, membership.UserId, membership.Email, ct);

                await notifications.RaiseAsync(RoleChangeNotification(
                    tenantId, membership, roleId, granted: false, actor: me.UserId), ct);
            }
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.MembersManage).WithAudit(AuditActions.MemberRoleRevoked, "membership");

        // Effective permission catalog: platform keys ∪ installed plugins' manifest
        // permissions ∪ per-site git repo permissions, each with a display name and
        // group — drives the role matrix.
        // Every entry says what it actually governs, so the role editor can show the
        // feature a permission belongs to and whether it is live in this tenant:
        //
        //  - platform keys link to the admin page they gate;
        //  - plugin keys name the *enabled instances* of that plugin. A plugin that
        //    ships in the binary but has no instance here has permissions that grant
        //    access to nothing, and they were previously indistinguishable from real
        //    ones — the catalog was built from manifests alone;
        //  - repo keys link to the site whose repository they cover.
        app.MapGet("/api/admin/permissions/catalog", async (
            IPluginCatalog catalog, SitesDbContext sites, CmsDbContext cms, CancellationToken ct) =>
        {
            var platform = PlatformPermissions.All.Select(k => new PermissionEntry(
                k, PlatformPermissionName(k), "Platform",
                new PermissionFeature("platform", null, "Platform", PlatformPermissionRoute(k), true, [])));

            // Instances the tenant has actually enabled, grouped by plugin.
            var instances = await cms.PluginInstances
                .Where(i => i.Enabled)
                .OrderBy(i => i.Name)
                .Select(i => new { i.PluginId, i.Id, i.Name, i.Slug })
                .ToListAsync(ct);
            var byPlugin = instances
                .GroupBy(i => i.PluginId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(i => new PermissionFeatureRef(i.Id.ToString(), string.IsNullOrWhiteSpace(i.Name) ? i.Slug : i.Name)).ToList());

            var plugin = catalog.Manifests.SelectMany(m =>
            {
                var used = byPlugin.TryGetValue(m.Id, out var list) ? list : [];
                return m.Permissions.Select(p => new PermissionEntry(
                    PlatformPermissions.ForPlugin(m.Id, p.Action),
                    p.DisplayName,
                    m.Name,
                    new PermissionFeature("plugin", m.Id, m.Name, "/plugins", used.Count > 0, used)));
            });

            // Per-site repo perms (git-backed sites — Mode A builder and Mode B React):
            // one read + one write key per site, so roles can grant pull/push on
            // individual repositories.
            var gitSites = await sites.Sites
                .Where(s => s.RenderMode == SiteRenderMode.ReactApp || s.RenderMode == SiteRenderMode.StaticPrerender)
                .OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name })
                .ToListAsync(ct);
            var repo = gitSites.SelectMany(s =>
            {
                var name = string.IsNullOrWhiteSpace(s.Name) ? s.Id.ToString() : s.Name;
                var feature = new PermissionFeature("site", s.Id.ToString(), name, $"/sites/{s.Id}", true, []);
                return new[]
                {
                    new PermissionEntry(PlatformPermissions.RepoRead(s.Id), $"{name}: clone/pull", "Repositories", feature),
                    new PermissionEntry(PlatformPermissions.RepoWrite(s.Id), $"{name}: push", "Repositories", feature),
                };
            });

            return Results.Ok(platform.Concat(plugin).Concat(repo));
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
        PlatformPermissions.AiChatsReadAll => "Read everyone's assistant chats",
        PlatformPermissions.AnalyticsRead => "View analytics",
        PlatformPermissions.ContentRead => "View content",
        PlatformPermissions.ContentWrite => "Edit content",
        PlatformPermissions.ContentPublish => "Publish content",
        PlatformPermissions.ChatRead => "View chat",
        PlatformPermissions.ChatManage => "Manage chat",
        _ => key,
    };

    /// <summary>Admin route a platform permission gates, for "what does this affect?".</summary>
    private static string? PlatformPermissionRoute(string key) => key switch
    {
        PlatformPermissions.TenantSettings => "/settings/general",
        PlatformPermissions.MembersManage => "/settings/members",
        PlatformPermissions.RolesManage => "/settings/roles",
        PlatformPermissions.DomainsManage => "/settings/domains",
        PlatformPermissions.PluginsManage => "/plugins",
        PlatformPermissions.MediaRead or PlatformPermissions.MediaWrite => "/media",
        PlatformPermissions.SiteEdit or PlatformPermissions.SitePublish => "/sites",
        PlatformPermissions.AiSettings => "/settings/ai",
        PlatformPermissions.AiChatsReadAll => "/assistant",
        PlatformPermissions.AnalyticsRead => "/analytics",
        PlatformPermissions.ContentRead or PlatformPermissions.ContentWrite
            or PlatformPermissions.ContentPublish => "/content",
        PlatformPermissions.ChatRead or PlatformPermissions.ChatManage => "/chat",
        _ => null,
    };

    /// <summary>One thing a permission grants access to (an instance, a site).</summary>
    private sealed record PermissionFeatureRef(string Id, string Name);

    /// <summary>
    /// What a permission governs. <paramref name="InUse"/> is false for a plugin that
    /// ships in the binary but has no enabled instance in this tenant — granting its
    /// permissions is harmless but meaningless, and the editor says so.
    /// </summary>
    private sealed record PermissionFeature(
        string Kind, string? Id, string Name, string? Route, bool InUse, IReadOnlyList<PermissionFeatureRef> Instances);

    private sealed record PermissionEntry(string Key, string DisplayName, string Group, PermissionFeature Feature);

    private static bool IsSlug(string value) =>
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && !value.StartsWith('-') && !value.EndsWith('-');

    private sealed record CreateTenantRequest(string Slug, string? Name, Guid? OwnerUserId, string? OwnerEmail);
    private sealed record CreateRoleRequest(string Name, string[] Permissions);
    private sealed record UpdateRoleRequest(string? Name, string[] Permissions);
    /// <summary>
    /// "Someone's access changed." Addressed to whoever manages members, and explicitly also
    /// to the affected member, who otherwise finds out by discovering a page has disappeared.
    /// Warning rather than Info: a permission change is the kind of thing a workspace owner
    /// should see even if it turns out to be routine.
    /// </summary>
    private static NotificationRequest RoleChangeNotification(
        Guid tenantId, TenantMembership membership, Guid roleId, bool granted, Guid? actor) =>
        new(
            TenantId: tenantId,
            Kind: NotificationKinds.MemberRoleChanged,
            Severity: NotificationSeverity.Warning,
            RequiredPermission: PlatformPermissions.MembersManage,
            TitleKey: NotificationKinds.TitleKey(NotificationKinds.MemberRoleChanged),
            BodyKey: NotificationKinds.BodyKey(NotificationKinds.MemberRoleChanged),
            // Not the membership alone: the same member can legitimately gain and lose the
            // same role repeatedly, and each of those is a distinct thing worth reporting.
            DedupeKey: $"member.role.changed:{membership.Id:N}:{roleId:N}:{(granted ? "grant" : "revoke")}:{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            Params: new { email = membership.Email, change = granted ? "granted" : "revoked" },
            LinkPath: "/settings/members",
            ResourceType: "membership",
            ResourceId: membership.Id,
            ActorUserId: actor,
            ExtraUserIds: [membership.UserId]);

    /// <summary>
    /// Sets a tenant's status, records it and tells the delivery plane.
    ///
    /// <para>The publish is not optional garnish. site-host caches domain -> tenant
    /// resolution, so without this a suspended tenant keeps serving its public site until an
    /// unrelated cache expiry — a suspension that works in the console and not in the world.
    /// It is published AFTER the commit, so a consumer can never observe a status the database
    /// has not accepted.</para>
    /// </summary>
    private static async Task<IResult> SetTenantStatusAsync(
        Guid tenantId,
        TenantStatus status,
        ConsoleCaller console,
        TenancyDbContext db,
        IAuditRecorder audit,
        IEventPublisher events,
        ILogger logger,
        CancellationToken ct)
    {
        if (!console.Allowed)
        {
            return Results.Forbid();
        }

        // IgnoreQueryFilters: tenants is not a tenant-scoped table, but this endpoint runs with
        // no ambient tenant at all, and being explicit costs nothing.
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == tenantId.ToString(), ct);

        if (tenant is null)
        {
            return Results.NotFound();
        }

        if (tenant.Status == status)
        {
            // Idempotent: the console can retry, and two operators can click at once, without
            // producing a second audit record for a change that did not happen.
            audit.Discard(audit.Declared!);
            return Results.NoContent();
        }

        var suspended = status == TenantStatus.Suspended;
        tenant.Status = status;

        // BEFORE SaveChangesAsync, and that ordering is load-bearing. The EF interceptor drains
        // the audit buffer into the transaction that commits the change, so anything added to
        // the entry afterwards is written nowhere -- the record still appears, silently missing
        // whatever the handler knew. Enriching first is what puts the new status in the row.
        audit.Declared?
            .Platform()
            .For("tenant", tenantId, tenant.Identifier)
            .With("status", status.ToString());

        await db.SaveChangesAsync(ct);

        // Best-effort, and deliberately not allowed to fail the request.
        //
        // The status is already committed by the line above. Throwing here would report failure
        // for a change that happened -- the worst answer available, because the operator retries
        // and the second call is a no-op that also looks wrong. The cost of a missed publish is
        // bounded and already understood: site-host's route cache expires within five minutes,
        // which is the same backstop SiteCacheInvalidator relies on. A suspension that takes
        // effect in under five minutes instead of instantly is a delay; a 500 that hides a
        // completed suspension is a lie.
        try
        {
            await events.PublishAsync(
                suspended ? Subjects.TenantSuspended : Subjects.TenantResumed,
                new TenantStatusChanged(
                    Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, tenant.Identifier, suspended),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Tenant {Slug} status committed as {Status}, but the invalidation event could not "
                + "be published. site-host will pick this up within its route-cache TTL.",
                tenant.Identifier,
                status);
        }

        return Results.NoContent();
    }

    private sealed record AssignRoleRequest(Guid RoleId);
}
