using System.Text.Json;
using Dcms.AdminApi.Environments;
using Dcms.PluginSdk.Abstractions;
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
            // The database's, not this process's cache: a job on the worker (a merged feature going,
            // a failed copy removed) changes environments this process does not hear of.
            environments.Evict(tenantId);
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

        // What duplicating can copy besides configuration; the personal ones are offered with a warning.
        app.MapGet("/api/admin/environments/copy-scopes", (EnvironmentDuplicator duplicator) =>
            Results.Ok(duplicator.Scopes.Select(x => new { scope = x.Scope, personalData = x.PersonalData })))
            .RequireAuthorization();

        // A new environment goes upstream of the current most-upstream one, which may then be
        // deployed into from it: adding staging, test and development in that order builds
        // development -> test -> staging -> production with no further setup.
        app.MapPost("/api/admin/environments", async (
            CreateEnvironmentRequest body, ITenantContext tenant, TenancyDbContext db, EnvironmentStore environments,
            CurrentUser me, IPermissionResolver permissions, WorkspaceMembers members, EnvironmentDuplicator duplicator,
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
            var scopes = (body.Copy ?? []).Distinct(StringComparer.Ordinal).ToList();
            if (scopes.Except(duplicator.Scopes.Select(x => x.Scope)).FirstOrDefault() is { } unknown)
            {
                return Results.BadRequest(new { error = $"Nothing called '{unknown}' can be copied." });
            }
            if (scopes.Count > 0 && body.Empty == true)
            {
                return Results.BadRequest(new { error = "An empty environment copies no data." });
            }
            // A copy -- its configuration, and whatever data is ticked -- needs a way into the
            // source, as reading it would.
            if (!me.IsSuperAdmin && !(await OpenedByAsync(db, tenantId, me.RequireUserId(), ct)).Contains(source.Id))
            {
                return Results.Forbid();
            }
            // A feature environment (development-feature1) is a branch of its source: it starts as
            // a copy of it, sits beside it and merges back into it.
            var feature = body.Feature == true;
            if (feature && body.Empty == true)
            {
                return Results.BadRequest(new { error = "A feature environment starts as a copy of what it branches from." });
            }

            // A stage goes in front of the pipeline; a feature beside what it branched from.
            // A service (billing, a shared catalogue) is outside the pipeline: the others use it
            // through links, and nothing deploys into or out of it unless set up so.
            var service = body.Service == true;
            if (service && feature)
            {
                return Results.BadRequest(new { error = "An environment is a feature or a service, not both." });
            }
            var upstream = feature ? source : all.MinBy(e => e.Order)!;
            var created = new TenantEnvironment
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Name = name,
                Order = service ? all.Max(e => e.Order) + 1 : upstream.Order - 1,
                Purpose = feature ? EnvironmentPurpose.Feature : service ? EnvironmentPurpose.Service : EnvironmentPurpose.Stage,
                ForkedFrom = feature ? source.Id : null,
                // A feature takes what its source does after the branch, so a clash on merging is
                // settled by bringing the source's work in first.
                PromotesFrom = feature ? [source.Id] : [],
                MergesOwnChangesOnly = body.MergesOwnChangesOnly ?? feature,
                DeleteWhenMerged = feature && body.DeleteWhenMerged == true,
                // A copy is closed until it is complete; an empty one is ready now.
                ReadyAt = body.Empty == true ? DateTimeOffset.UtcNow : null,
            };
            audit.Declared?.For("environment", created.Id, name).With("from", source.Name).With("copy", scopes);
            using (RlsScope.Tenant(tenantId))
            {
                db.Environments.Add(created);
                if (!service)
                {
                    var tracked = await db.Environments.IgnoreTenantFilter().FirstAsync(e => e.TenantId == tenantId && e.Id == upstream.Id, ct);
                    tracked.PromotesFrom = [.. tracked.PromotesFrom, created.Id];
                }
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
                    EnvironmentCopyJob.Kind, tenantId, new EnvironmentCopyPayload(source.Id, scopes),
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
            if (body.MergesOwnChangesOnly is { } own) target.MergesOwnChangesOnly = own;
            if (body.DeleteWhenMerged is { } delete) target.DeleteWhenMerged = delete && target.ForkedFrom is not null;
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
                .With("promotes_from", target.PromotesFrom)
                .With("merges_own_changes_only", target.MergesOwnChangesOnly)
                .With("delete_when_merged", target.DeleteWhenMerged);
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

        // Links (ADR 0026, 0028): this environment using another's resource, within the workspace --
        // an app's /api, sign-in, contract operations, or Dynamic Apps tables.
        app.MapGet("/api/admin/environments/{id:guid}/links", async (
            Guid id, ITenantContext tenant, EnvironmentStore environments, TenancyDbContext db, CancellationToken ct) =>
        {
            if (await TargetAsync(environments, tenant, id, ct) is not { } target) return Results.NotFound();
            using var rls = target.Scope();
            return Results.Ok(await db.EnvironmentLinks.IgnoreTenantFilter().AsNoTracking()
                .Where(l => l.TenantId == target.TenantId && l.EnvironmentId == target.Id)
                .Select(l => new { id = l.Id, kind = l.Kind, targetEnvironmentId = l.TargetEnvironmentId, targetKey = l.TargetKey, access = l.Access.ToString(), grants = l.Grants })
                .ToListAsync(ct));
        }).RequireAuthorization();

        // What another environment can use from this one through a link: every enabled app -- its
        // address (app links), operations (contracts links) and tables (tables links), as the grants
        // that name them -- so a link is picked from what is really there.
        app.MapGet("/api/admin/environments/{id:guid}/offers", async (
            Guid id, ITenantContext tenant, EnvironmentStore environments, Dcms.Shared.Data.Cms.CmsDbContext cms,
            Dcms.PluginSdk.Runtime.PluginRegistry registry, IEnumerable<IEnvironmentLinkTables> linkTables, CancellationToken ct) =>
        {
            if (await TargetAsync(environments, tenant, id, ct) is not { } target) return Results.NotFound();
            using var rls = target.Scope();
            var instances = await cms.PluginInstances.IgnoreTenantFilter().AsNoTracking()
                .Where(p => p.TenantId == target.TenantId && p.Enabled).OrderBy(p => p.Slug)
                .Select(p => new { p.Id, p.Slug, p.Name, p.PluginId }).ToListAsync(ct);
            var offers = new List<object>();
            foreach (var i in instances)
            {
                var operations = EnvironmentLinkRules.Operations(registry, i.PluginId)
                    .Select(o => new { grant = o.Grant, risk = o.Risk.ToString().ToLowerInvariant(), description = o.Description }).ToList();
                var tables = linkTables.FirstOrDefault(t => t.PluginId == i.PluginId) is { } provider
                    ? await provider.TablesAsync(target.TenantId, i.Id, ct)
                    : [];
                offers.Add(new { slug = i.Slug, name = i.Name, pluginId = i.PluginId, operations, tables });
            }
            return Results.Ok(offers);
        }).RequireAuthorization();

        app.MapPost("/api/admin/environments/{id:guid}/links", async (
            Guid id, LinkRequest body, ITenantContext tenant, EnvironmentStore environments, TenancyDbContext db,
            Dcms.Shared.Data.Cms.CmsDbContext cms, CurrentUser me, IPermissionResolver permissions,
            Dcms.Shared.Caching.ICacheService cache, IAuditRecorder audit, IEnumerable<IEnvironmentLinksObserver> observers,
            Dcms.PluginSdk.Runtime.PluginRegistry registry, IEnumerable<IEnvironmentLinkTables> linkTables, CancellationToken ct) =>
        {
            if (await TargetAsync(environments, tenant, id, ct) is not { } consumer) return Results.NotFound();
            var tenantId = consumer.TenantId;
            if (!await CanManageAsync(tenantId, me, permissions, ct)) return Results.Forbid();
            if (await environments.GetAsync(tenantId, body.TargetEnvironmentId, ct) is not { } provider)
            {
                return Results.BadRequest(new { error = "A link names another environment of this workspace." });
            }
            var (link, refused) = await EnvironmentLinkRules.CheckAsync(db, cms, registry, linkTables, consumer, provider,
                body.Kind, body.TargetKey, body.Access, body.Grants, existing: null, ct);
            if (refused is not null)
            {
                return refused.Conflict ? Results.Conflict(new { error = refused.Error }) : Results.BadRequest(new { error = refused.Error });
            }
            using var scope = consumer.Scope();
            var row = new EnvironmentLink
            {
                Id = Guid.NewGuid(), TenantId = tenantId, EnvironmentId = consumer.Id, TargetEnvironmentId = provider.Id, Kind = link!.Kind,
                TargetKey = link.TargetKey, Access = link.Access, Grants = link.Grants,
            };
            db.EnvironmentLinks.Add(row);
            audit.Declared?.For("environment_link", row.Id, row.TargetKey).With("environment", consumer.Name)
                .With("target", provider.Name).With("access", row.Access.ToString()).With("grants", row.Grants);
            await db.SaveChangesAsync(ct);
            await cache.RemoveAsync(EnvironmentLinkKinds.CacheKey(consumer.Id), ct);
            foreach (var observer in observers) await observer.LinkChangedAsync(tenantId, consumer.Id, provider.Id, row.Kind, ct);
            return Results.Created($"/api/admin/environments/{id}/links/{row.Id}", new { id = row.Id });
        }).RequireAuthorization().PermissionExempt(ProductionGate).WithAudit(AuditActions.EnvironmentLinkCreated, "environment_link");

        app.MapDelete("/api/admin/environments/{id:guid}/links/{linkId:guid}", async (
            Guid id, Guid linkId, ITenantContext tenant, EnvironmentStore environments, TenancyDbContext db, CurrentUser me,
            IPermissionResolver permissions, Dcms.Shared.Caching.ICacheService cache, IAuditRecorder audit,
            IEnumerable<IEnvironmentLinksObserver> observers, CancellationToken ct) =>
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
            foreach (var observer in observers) await observer.LinkChangedAsync(consumer.TenantId, consumer.Id, link.TargetEnvironmentId, link.Kind, ct);
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
        forkedFrom = e.ForkedFrom,
        ready = e.IsReady,
        setup = e.SetupJson is { } setup ? JsonSerializer.Deserialize<List<SetupStep>>(setup, EnvironmentSetupLog.Json) : null,
        mergesOwnChangesOnly = e.MergesOwnChangesOnly,
        deleteWhenMerged = e.DeleteWhenMerged,
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
    /// <param name="Copy">Data scopes to copy from it too (records, submissions, …): a duplicate rather than a fresh copy.</param>
    /// <param name="Feature">A feature environment: a branch of <paramref name="From"/>, merging back into it.</param>
    /// <param name="MergesOwnChangesOnly">Deploying from it offers only the migrations cut in it; a feature's default.</param>
    /// <param name="DeleteWhenMerged">A feature deleted once everything it cut is deployed into what it branched from.</param>
    /// <param name="Service">A service environment: outside the pipeline, used by the others through links.</param>
    private sealed record CreateEnvironmentRequest(
        string? Name, bool? Empty, Guid? From, List<string>? Copy, bool? Feature, bool? MergesOwnChangesOnly, bool? DeleteWhenMerged,
        bool? Service);
    private sealed record UpdateEnvironmentRequest(
        string? Name, int? Order, List<Guid>? PromotesFrom, bool? MergesOwnChangesOnly, bool? DeleteWhenMerged);
    private sealed record PolicyRequest(string Kind, string? ItemKey, string Mode);
    /// <param name="Grants">What a contracts link may call (<c>contract/Operation</c>) or a tables link may do (<c>table:verb</c>).</param>
    private sealed record LinkRequest(string Kind, Guid TargetEnvironmentId, string? TargetKey, string? Access, List<string>? Grants);

}
