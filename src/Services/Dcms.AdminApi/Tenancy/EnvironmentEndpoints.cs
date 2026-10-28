using Dcms.AdminApi.Environments;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Jobs;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// A workspace's environments (ADR 0028): rows of <c>tenancy.environments</c>, one of them
/// production, whose id is the tenant's. A tenant with production alone has environments "off".
///
/// <para>Managing them is the workspace's business, so <c>environments:manage</c> is checked in
/// production whichever environment the caller is in -- a role that can manage environments in
/// development must not be able to add or delete them from there.</para>
/// </summary>
public static class EnvironmentEndpoints
{
    public static IEndpointRouteBuilder MapEnvironmentEndpoints(this IEndpointRouteBuilder app)
    {
        // Any member sees the whole list: it is how they switch. Which ones they can open is
        // isMember -- a role there, once there is more than production. Exempt from the role gate
        // of the environment the request is in, because a member with no role there needs this
        // to find one they have: the workspace membership is checked here instead.
        app.MapGet("/api/admin/environments", async (
            ITenantContext tenant, EnvironmentStore environments, TenancyDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId) return Results.BadRequest(new { error = "Select a workspace first." });
            if (!me.IsSuperAdmin)
            {
                var userId = me.RequireUserId();
                // rls: request tenant -- memberships are the workspace's, named by tenant and user.
                if (!await db.Memberships.IgnoreTenantFilter().AsNoTracking().AnyAsync(m => m.TenantId == tenantId && m.UserId == userId, ct))
                {
                    return Results.Forbid();
                }
            }
            var all = await environments.ListAsync(tenantId, ct);
            var opens = me.IsSuperAdmin || all.Count == 1
                ? all.Select(e => e.Id).ToHashSet()
                : await OpenedByAsync(db, tenantId, me.RequireUserId(), ct);
            return Results.Ok(new
            {
                enabled = all.Count > 1,
                workspaceId = tenantId,
                environments = all.Select(e => View(e, opens.Contains(e.Id))),
            });
        }).RequireAuthorization().AllowNonMemberTenant(
            "A workspace member with no role in the environment named, or in production, lists the environments to find one they "
            + "can open; the handler checks workspace membership itself.");

        // A new environment goes upstream of the current most-upstream one, which may then be
        // deployed into from it: adding staging, test and development in that order builds
        // development -> test -> staging -> production with no further setup.
        app.MapPost("/api/admin/environments", async (
            CreateEnvironmentRequest body, ITenantContext tenant, TenancyDbContext db, EnvironmentStore environments,
            CurrentUser me, IPermissionResolver permissions, WorkspaceMembers members,
            IJobQueue jobs, IAuditRecorder audit, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId) return Results.BadRequest(new { error = "Select a workspace first." });
            if (!await CanManageAsync(tenantId, me, permissions, ct)) return Results.Forbid();

            var name = body.Name?.Trim().ToLowerInvariant() ?? "";
            if (!IsEnvironmentName(name))
            {
                return Results.BadRequest(new { error = "The name must be 1-32 lowercase letters, digits or dashes." });
            }
            var all = await environments.ListAsync(tenantId, ct);
            if (all.Count >= MaxEnvironments)
            {
                return Results.Conflict(new { error = $"A workspace can have at most {MaxEnvironments} environments." });
            }
            if (all.Any(e => e.Name == name))
            {
                return Results.Conflict(new { error = "The workspace already has an environment of that name." });
            }
            var source = body.From is { } from ? all.FirstOrDefault(e => e.Id == from) : all.Single(e => e.IsProduction);
            if (source is null) return Results.BadRequest(new { error = "There is no environment to copy from of that id." });

            var upstream = all.MinBy(e => e.Order)!;
            var created = new TenantEnvironment
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Name = name, Order = upstream.Order - 1,
            };
            audit.Declared?.For("environment", created.Id, name);
            using (RlsScope.Tenant(tenantId))
            {
                db.Environments.Add(created);
                var tracked = await db.Environments.IgnoreTenantFilter().FirstAsync(e => e.TenantId == tenantId && e.Id == upstream.Id, ct);
                tracked.PromotesFrom = [.. tracked.PromotesFrom, created.Id];
                await db.SaveChangesAsync(ct);
            }
            environments.Evict(tenantId);
            // Its roles and who holds them come from the source, so everyone keeps the access they
            // had there; its configuration follows as a job (a site's repository is pushed;
            // plugins are set up): the environment exists now and fills in over the next moments.
            await members.SeedAsync(created, source, ct);
            if (body.Empty != true)
            {
                await jobs.EnqueueAsync(new JobSpec(
                    EnvironmentCopyJob.Kind, tenantId, new EnvironmentCopyPayload(source.Id),
                    "jobs.kinds.environmentCopy.title", new { environment = name },
                    RequiredPermission: PlatformPermissions.EnvironmentsManage, MaxAttempts: 1,
                    LinkPath: "/deployments") { EnvironmentId = created.Id }, ct);
            }

            return Results.Created($"/api/admin/environments/{created.Id}", View(created, true));
        }).RequireAuthorization().PermissionExempt(ProductionGate).WithAudit(AuditActions.EnvironmentCreated, "environment");

        app.MapPatch("/api/admin/environments/{id:guid}", async (
            Guid id, UpdateEnvironmentRequest body, ITenantContext tenant, TenancyDbContext db, EnvironmentStore environments,
            CurrentUser me, IPermissionResolver permissions, IAuditRecorder audit, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId) return Results.NotFound();
            if (!await CanManageAsync(tenantId, me, permissions, ct)) return Results.Forbid();
            using var rls = RlsScope.Tenant(tenantId);
            var all = await db.Environments.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ToListAsync(ct);
            var target = all.FirstOrDefault(e => e.Id == id);
            if (target is null) return Results.NotFound();

            if (body.Name is not null)
            {
                var name = body.Name.Trim().ToLowerInvariant();
                if (!IsEnvironmentName(name))
                {
                    return Results.BadRequest(new { error = "The name must be 1-32 lowercase letters, digits or dashes." });
                }
                // Production is what a request naming no environment gets; its name is fixed.
                if (target.IsProduction && name != target.Name)
                {
                    return Results.Conflict(new { error = "The production environment keeps its name." });
                }
                if (all.Any(e => e.Id != id && e.Name == name))
                {
                    return Results.Conflict(new { error = "The workspace already has an environment of that name." });
                }
                target.Name = name;
            }
            if (body.Order is { } order) target.Order = order;
            if (body.PromotesFrom is { } sources)
            {
                var ids = all.Select(e => e.Id).ToHashSet();
                if (sources.Any(s => s == id || !ids.Contains(s)))
                {
                    return Results.BadRequest(new { error = "An environment can only be deployed into from another environment of this workspace." });
                }
                target.PromotesFrom = sources.Distinct().ToList();
            }
            audit.Declared?.For("environment", target.Id, target.Name)
                .With("name", target.Name)
                .With("order", target.Order)
                .With("promotes_from", target.PromotesFrom);
            await db.SaveChangesAsync(ct);
            environments.Evict(tenantId);
            return Results.Ok(View(target, true));
        }).RequireAuthorization().PermissionExempt(ProductionGate).WithAudit(AuditActions.EnvironmentUpdated, "environment");

        app.MapDelete("/api/admin/environments/{id:guid}", async (
            Guid id, ITenantContext tenant, CurrentUser me,
            IPermissionResolver permissions, TenantDeleter deleter, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId) return Results.NotFound();
            if (!await CanManageAsync(tenantId, me, permissions, ct)) return Results.Forbid();
            if (id == tenantId)
            {
                return Results.Conflict(new { error = "The production environment is the workspace; delete the workspace instead." });
            }
            var result = await deleter.DeleteEnvironmentAsync(tenantId, id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization().PermissionExempt(ProductionGate)
          .AuditExempt("TenantDeleter.DeleteEnvironmentAsync records environment.deleted itself, with the manifest of what went.");

        // Who owns each kind of configuration in an environment, or one item of it (ADR 0026).
        app.MapGet("/api/admin/environments/{id:guid}/policies", async (
            Guid id, ITenantContext tenant, EnvironmentStore environments, TenancyDbContext db, CancellationToken ct) =>
        {
            if (await TargetAsync(environments, tenant, id, ct) is not { } target) return Results.NotFound();
            using var rls = target.Scope();
            var policies = await db.EnvironmentPolicies.IgnoreTenantFilter().AsNoTracking()
                .Where(p => p.TenantId == target.TenantId && p.EnvironmentId == target.Id)
                .Select(p => new { kind = p.Kind, itemKey = p.ItemKey, mode = p.Mode.ToString() })
                .ToListAsync(ct);
            return Results.Ok(policies);
        }).RequireAuthorization();

        // Replaces the environment's policies with the given set; anything left out is Mixed.
        app.MapPut("/api/admin/environments/{id:guid}/policies", async (
            Guid id, List<PolicyRequest> body, ITenantContext tenant, EnvironmentStore environments, TenancyDbContext db, CurrentUser me,
            IPermissionResolver permissions, Dcms.AdminApi.Sites.Git.SiteGitService git, Dcms.Shared.Data.Sites.SitesDbContext sites,
            IAuditRecorder audit, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (await TargetAsync(environments, tenant, id, ct) is not { } target) return Results.NotFound();
            if (!await CanManageAsync(target.TenantId, me, permissions, ct)) return Results.Forbid();
            var wanted = new List<EnvironmentPolicy>();
            foreach (var p in body)
            {
                if (string.IsNullOrWhiteSpace(p.Kind) || p.Kind.Length > 64 || p.ItemKey?.Length > 256
                    || !Enum.TryParse<EnvironmentOwnership>(p.Mode, ignoreCase: true, out var mode))
                {
                    return Results.BadRequest(new { error = "Each policy names a kind and a mode (mixed, local or managed)." });
                }
                if (mode != EnvironmentOwnership.Mixed && wanted.All(w => w.Kind != p.Kind || w.ItemKey != p.ItemKey))
                {
                    wanted.Add(new EnvironmentPolicy
                    {
                        Id = Guid.NewGuid(), TenantId = target.TenantId, EnvironmentId = target.Id, Kind = p.Kind, ItemKey = p.ItemKey, Mode = mode,
                    });
                }
            }

            using (target.Scope())
            {
                db.EnvironmentPolicies.RemoveRange(await db.EnvironmentPolicies.IgnoreTenantFilter()
                    .Where(p => p.TenantId == target.TenantId && p.EnvironmentId == target.Id).ToListAsync(ct));
                db.EnvironmentPolicies.AddRange(wanted);
                audit.Declared?.For("environment", target.Id, target.Name).With("policies", wanted.Select(w => $"{w.Kind}/{w.ItemKey ?? "*"}={w.Mode}"));
                await db.SaveChangesAsync(ct);

                // A site left to deployments takes code from them only, at the git server too.
                var unprotected = new List<string>();
                if (git.Enabled)
                {
                    var repos = await sites.Sites.IgnoreTenantFilter().AsNoTracking()
                        .Where(s => s.TenantId == target.TenantId && s.GitRepoFullName != null).ToListAsync(ct);
                    foreach (var site in repos)
                    {
                        var locked = EnvironmentPolicies.Ownership(wanted, "sites", site.KeyAcrossEnvironments) == EnvironmentOwnership.Managed;
                        try
                        {
                            await git.LockReleaseAsync(site.GitRepoFullName!, locked, ct);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
                        {
                            logs.CreateLogger("Environments").LogWarning(ex, "Could not set release protection on {Repo}.", site.GitRepoFullName);
                            unprotected.Add(site.GitRepoFullName!);
                        }
                    }
                }
                if (unprotected.Count > 0)
                {
                    // Not quietly: the policy says only deployments push here, and git does not yet agree.
                    return Results.Json(new
                    {
                        error = "The policy is saved, but the git server did not take it for these repositories. Save again to retry.",
                        repositories = unprotected,
                    }, statusCode: StatusCodes.Status502BadGateway);
                }
            }
            return Results.NoContent();
        }).RequireAuthorization().PermissionExempt(ProductionGate).WithAudit(AuditActions.EnvironmentPoliciesChanged, "environment");

        // Links (ADR 0026): this environment using another's resource, within the workspace.
        app.MapGet("/api/admin/environments/{id:guid}/links", async (
            Guid id, ITenantContext tenant, EnvironmentStore environments, TenancyDbContext db, CancellationToken ct) =>
        {
            if (await TargetAsync(environments, tenant, id, ct) is not { } target) return Results.NotFound();
            using var rls = target.Scope();
            return Results.Ok(await db.EnvironmentLinks.IgnoreTenantFilter().AsNoTracking()
                .Where(l => l.TenantId == target.TenantId && l.EnvironmentId == target.Id)
                .Select(l => new { id = l.Id, kind = l.Kind, targetEnvironmentId = l.TargetEnvironmentId, targetKey = l.TargetKey, access = l.Access.ToString() })
                .ToListAsync(ct));
        }).RequireAuthorization();

        app.MapPost("/api/admin/environments/{id:guid}/links", async (
            Guid id, LinkRequest body, ITenantContext tenant, EnvironmentStore environments, TenancyDbContext db,
            Dcms.Shared.Data.Cms.CmsDbContext cms, CurrentUser me, IPermissionResolver permissions,
            Dcms.Shared.Caching.ICacheService cache, IAuditRecorder audit, CancellationToken ct) =>
        {
            if (await TargetAsync(environments, tenant, id, ct) is not { } consumer) return Results.NotFound();
            var tenantId = consumer.TenantId;
            if (!await CanManageAsync(tenantId, me, permissions, ct)) return Results.Forbid();
            if (body.TargetEnvironmentId == id || await environments.GetAsync(tenantId, body.TargetEnvironmentId, ct) is not { } provider)
            {
                return Results.BadRequest(new { error = "A link names another environment of this workspace." });
            }
            EnvironmentLinkAccess access;
            if (body.Kind == EnvironmentLinkKinds.Users)
            {
                // Sign-in through the target's realm. One realm per environment, and never a
                // chain: the target signs its own people in, and nobody signs in through this one.
                access = EnvironmentLinkAccess.Read;
                // rls: platform -- the workspace's users links, every environment's, named by tenant.
                using var platform = RlsScope.Platform();
                if (await db.EnvironmentLinks.IgnoreQueryFilters().AnyAsync(l => l.TenantId == tenantId && l.Kind == EnvironmentLinkKinds.Users
                        && (l.EnvironmentId == id || l.EnvironmentId == provider.Id || l.TargetEnvironmentId == id), ct))
                {
                    return Results.Conflict(new { error = "An environment takes its users from one other environment, which keeps its own." });
                }
            }
            else
            {
                if (body.Kind != EnvironmentLinkKinds.App || string.IsNullOrWhiteSpace(body.TargetKey)
                    || !Enum.TryParse(body.Access, ignoreCase: true, out access))
                {
                    return Results.BadRequest(new { error = "An app link names the app's address and read or readWrite access." });
                }
                // Only an app the target has: anything else under /api is the platform's own routes.
                using (provider.Scope())
                {
                    if (!await cms.PluginInstances.IgnoreTenantFilter().AnyAsync(p => p.TenantId == tenantId && p.Slug == body.TargetKey, ct))
                    {
                        return Results.BadRequest(new { error = "That environment has no app at this address." });
                    }
                }
                using var rls = consumer.Scope();
                if (await db.EnvironmentLinks.IgnoreTenantFilter().AnyAsync(l => l.TenantId == tenantId && l.Kind == body.Kind && l.TargetKey == body.TargetKey, ct))
                {
                    return Results.Conflict(new { error = "This app is already linked." });
                }
            }
            using var scope = consumer.Scope();
            var link = new EnvironmentLink
            {
                Id = Guid.NewGuid(), TenantId = tenantId, EnvironmentId = consumer.Id, TargetEnvironmentId = provider.Id, Kind = body.Kind,
                TargetKey = body.Kind == EnvironmentLinkKinds.Users ? null : body.TargetKey!.Trim(), Access = access,
            };
            db.EnvironmentLinks.Add(link);
            audit.Declared?.For("environment_link", link.Id, link.TargetKey).With("environment", consumer.Name)
                .With("target", provider.Name).With("access", access.ToString());
            await db.SaveChangesAsync(ct);
            await cache.RemoveAsync(EnvironmentLinkKinds.CacheKey(consumer.Id), ct);
            return Results.Created($"/api/admin/environments/{id}/links/{link.Id}", new { id = link.Id });
        }).RequireAuthorization().PermissionExempt(ProductionGate).WithAudit(AuditActions.EnvironmentLinkCreated, "environment_link");

        app.MapDelete("/api/admin/environments/{id:guid}/links/{linkId:guid}", async (
            Guid id, Guid linkId, ITenantContext tenant, EnvironmentStore environments, TenancyDbContext db, CurrentUser me,
            IPermissionResolver permissions, Dcms.Shared.Caching.ICacheService cache, IAuditRecorder audit, CancellationToken ct) =>
        {
            if (await TargetAsync(environments, tenant, id, ct) is not { } consumer) return Results.NotFound();
            if (!await CanManageAsync(consumer.TenantId, me, permissions, ct)) return Results.Forbid();
            using var rls = consumer.Scope();
            var link = await db.EnvironmentLinks.IgnoreTenantFilter()
                .FirstOrDefaultAsync(l => l.TenantId == consumer.TenantId && l.EnvironmentId == consumer.Id && l.Id == linkId, ct);
            if (link is null) return Results.NotFound();
            db.EnvironmentLinks.Remove(link);
            audit.Declared?.For("environment_link", link.Id, link.TargetKey).With("environment", consumer.Name);
            await db.SaveChangesAsync(ct);
            await cache.RemoveAsync(EnvironmentLinkKinds.CacheKey(consumer.Id), ct);
            return Results.NoContent();
        }).RequireAuthorization().PermissionExempt(ProductionGate).WithAudit(AuditActions.EnvironmentLinkDeleted, "environment_link");

        return app;
    }

    private static object View(TenantEnvironment e, bool isMember) => new
    {
        id = e.Id,
        name = e.Name,
        order = e.Order,
        purpose = e.Purpose.ToString().ToLowerInvariant(),
        isProduction = e.IsProduction,
        promotesFrom = e.PromotesFrom,
        isMember,
    };

    private static async Task<TenantEnvironment?> TargetAsync(EnvironmentStore environments, ITenantContext tenant, Guid id, CancellationToken ct) =>
        tenant.TenantId is { } tenantId ? await environments.GetAsync(tenantId, id, ct) : null;

    /// <summary>The environments the user holds a role in.</summary>
    private static async Task<HashSet<Guid>> OpenedByAsync(TenancyDbContext db, Guid tenantId, Guid userId, CancellationToken ct)
    {
        // rls: platform -- the caller's own role assignments in every environment of this tenant,
        // named by tenant and user.
        using var rls = RlsScope.Platform();
        return (await db.MemberRoles.IgnoreQueryFilters()
                .Where(r => r.TenantId == tenantId
                            && db.Memberships.IgnoreQueryFilters().Any(m => m.Id == r.MembershipId && m.TenantId == tenantId && m.UserId == userId))
                .Select(r => r.EnvironmentId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
    }

    /// <summary>environments:manage, held in production.</summary>
    private static async Task<bool> CanManageAsync(
        Guid tenantId, CurrentUser me, IPermissionResolver permissions, CancellationToken ct)
    {
        if (me.IsSuperAdmin) return true;
        if (me.UserId is not { } userId) return false;
        using var production = RlsScope.Environment(tenantId, tenantId);
        return (await permissions.GetPermissionsAsync(tenantId, userId, ct)).Contains(PlatformPermissions.EnvironmentsManage);
    }

    // The gate the endpoint table cannot see: environments:manage held in production, not in the
    // environment the request names.
    private const string ProductionGate = "environments:manage in the production environment, checked in the handler (CanManageAsync).";

    // ponytail: a fixed cap; per-plan limits if environments are ever sold by the count.
    private const int MaxEnvironments = 10;

    private static bool IsEnvironmentName(string value) =>
        value.Length is > 0 and <= 32
        && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && !value.StartsWith('-') && !value.EndsWith('-');

    /// <param name="Empty">True for an environment without the source's configuration.</param>
    /// <param name="From">The environment whose roles and configuration it starts with; production when absent.</param>
    private sealed record CreateEnvironmentRequest(string? Name, bool? Empty, Guid? From);
    private sealed record UpdateEnvironmentRequest(string? Name, int? Order, List<Guid>? PromotesFrom);
    private sealed record PolicyRequest(string Kind, string? ItemKey, string Mode);
    private sealed record LinkRequest(string Kind, Guid TargetEnvironmentId, string? TargetKey, string? Access);
}
