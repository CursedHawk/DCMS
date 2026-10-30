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

/// <summary>Summary of what a tenant delete removed, for the operator's confirmation.</summary>
public sealed record TenantDeletionResult(
    Guid TenantId,
    int SitesDeleted,
    int ObjectsDeleted,
    bool OrgDeleted,
    bool StorageFailed);

/// <summary>
/// Deletes a tenant and everything owned by it, across every schema.
///
/// Sites go through <see cref="SiteDeleter"/> first so their git repos, build
/// artifacts and per-site role permissions are cleaned up by the code that already
/// knows how; the rest is a per-schema sweep on TenantId. Query filters are bypassed
/// throughout because the ambient tenant is being removed underneath us.
///
/// Same ordering rule as site deletion: databases first, external systems after,
/// best-effort. Leaked objects are recoverable by hand; a tenant row that no longer
/// matches its storage is not.
/// </summary>
public sealed class TenantDeleter(
    TenancyDbContext tenancy,
    SitesDbContext sites,
    CmsDbContext cms,
    MediaDbContext media,
    FormsDbContext forms,
    AppsDbContext apps,
    FunctionsDbContext functions,
    UserAuthDbContext userAuth,
    SearchDbContext search,
    AnalyticsDbContext analytics,
    ChatDbContext chat,
    AiDbContext ai,
    VisitorsDbContext visitors,
    SocialDbContext social,
    NotificationsDbContext notifications,
    TenantStore store,
    EnvironmentStore environmentStore,
    SiteDeleter siteDeleter,
    SiteGitService git,
    RealmAdminClient realms,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    IAuditRecorder audit,
    AuditScope scope,
    ILogger<TenantDeleter> logger)
{
    public async Task<TenantDeletionResult?> DeleteAsync(Guid tenantId, CancellationToken ct)
    {
        // Every statement below is keyed on this one tenant, whether an owner or the platform
        // console asked for it -- so the database is told exactly that, not "all tenants". A
        // delete that lost its predicate empties this tenant's table, not everyone's (ADR 0015).
        using var rls = RlsScope.Tenant(tenantId);
        var key = tenantId.ToString();
        var tenant = await tenancy.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == key, ct);
        if (tenant is null)
        {
            return null;
        }
        var slug = tenant.Identifier;

        // The intent, written and confirmed before a single row is destroyed. This is the one
        // place on the platform that refuses to act unless it can record first: everything
        // below is irreversible, and RecordNowAsync propagates a sink failure rather than
        // swallowing it, so a purge that cannot be recorded does not happen.
        await audit.RecordNowAsync(
            new AuditEntry { Action = AuditActions.TenantPurgeStarted }
                .InTenant(tenantId)
                .For("tenant", tenantId, slug)
                .As(AuditCategory.Security, AuditSeverity.Critical),
            ct);

        // From here every statement is set-based and every schema is emptied. Per-statement
        // records would be twenty-five rows saying a table got shorter; one manifest below
        // says what was destroyed, which is the question anyone will actually ask.
        using var _ = scope.SuppressBulkCapture();
        var manifest = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Each environment's own rows, read and deleted as that environment (ADR 0028), the
        // others before production.
        var environments = await TenancyPermissionResolver.EnvironmentIdsAsync(tenancy, tenantId, ct);
        var siteCount = 0;
        foreach (var environmentId in environments.AsEnumerable().Reverse())
        {
            siteCount += await SweepEnvironmentAsync(tenantId, environmentId, manifest, ct);
        }

        // Then what the workspace shares.
        await SweepAsync(media, manifest, ct,
            ("media.Variants", () => media.Variants.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("media.Assets", () => media.Assets.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("media.Folders", () => media.Folders.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(analytics, manifest, ct,
            ("analytics.Events", () => analytics.Events.Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("analytics.DailyRollups", () => analytics.DailyRollups.Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        // BUG-04: the transcripts hold whole AI tool results (draft content, analytics figures)
        // lifted out of the tenant's data, so a purge that left them behind was an erasure gap.
        // Messages and runs are deleted before their conversation so no orphan survives a DB
        // whose cascade is not configured.
        await SweepAsync(ai, manifest, ct,
            ("ai.Messages", () => ai.Messages.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("ai.Runs", () => ai.Runs.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("ai.Conversations", () => ai.Conversations.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("ai.UserSettings", () => ai.UserSettings.Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("ai.Settings", () => ai.Settings.Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        // BUG-04: the tenant's in-app notifications (the platform_* tables are not tenant-scoped).
        await SweepAsync(notifications, manifest, ct,
            ("notifications.Recipients", () => notifications.Recipients.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("notifications.Notifications", () => notifications.Notifications.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(tenancy, manifest, ct,
            ("tenancy.Memberships", () => tenancy.Memberships.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.Invitations", () => tenancy.Invitations.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.Usage", () => tenancy.Usage.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.Environments", () => tenancy.Environments.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.Tenants", () => tenancy.Tenants.Where(t => t.Id == key).ExecuteDeleteAsync(ct)));
        // Resolution is cached for 30 s (TenantStore). Without this the deleted tenant keeps
        // resolving here -- and a new tenant created under the same slug meanwhile resolves to
        // the dead one, so its writes land under a TenantId nothing can read back.
        store.Evict(tenant);
        environmentStore.Evict(tenantId);

        // --- External systems (best effort) ---
        var objects = 0;
        var storageFailed = false;
        foreach (var (bucket, prefix) in new[]
                 {
                     (storageOptions.Value.MediaBucket, $"tenants/{tenantId}/"),
                     (storageOptions.Value.SitesBucket, $"{tenantId}/"),
                     (storageOptions.Value.BuildLogsBucket, $"{tenantId}/"),
                 })
        {
            try
            {
                objects += await storage.DeletePrefixAsync(bucket, prefix, ct);
            }
            catch (Exception ex)
            {
                storageFailed = true;
                logger.LogError(ex, "Failed deleting {Bucket}/{Prefix} for deleted tenant {TenantId}.",
                    bucket, prefix, tenantId);
            }
        }

        var orgDeleted = await git.DeleteOrgAsync(slug, ct);

        // The tenant's realm in identity (ADR 0022): its site users, their logins, its sign-in
        // client and every token that client issued. Best effort like the rest of this block;
        // a realm left behind is reported in the manifest, and holds no tenant content.
        var realmDeleted = true;
        foreach (var environmentId in environments)
        {
            realmDeleted &= await DeleteRealmAsync(environmentId, ct);
        }

        logger.LogWarning("Tenant {TenantId} ({Slug}) deleted: {Sites} sites, {Objects} objects.",
            tenantId, slug, siteCount, objects);

        // The manifest. Written last, because it is the only surviving description of what the
        // tenant held — the rows it counts no longer exist to be counted again. It is stamped
        // with the tenant explicitly: the ambient tenant has just been deleted underneath us,
        // and a record of a purge that landed on the platform scope would be lost to the one
        // person entitled to ask about it.
        audit.Record(AuditActions.TenantPurged)
            .InTenant(tenantId)
            .For("tenant", tenantId, slug)
            .As(AuditCategory.Security, AuditSeverity.Critical)
            .With("rows", manifest)
            .With("sites", siteCount)
            .With("objects", objects)
            .With("git_org_deleted", orgDeleted)
            .With("realm_deleted", realmDeleted)
            .With("storage_failed", storageFailed);
        await audit.FlushAsync(ct);

        return new TenantDeletionResult(tenantId, siteCount, objects, orgDeleted, storageFailed);
    }

    /// <summary>
    /// Deletes one environment other than production (ADR 0028): its own rows, sites, realm and
    /// plugin files, and every reference the tenant's other environments hold to it. What the
    /// workspace shares -- media, members, the audit log -- is untouched.
    /// </summary>
    public async Task<TenantDeletionResult?> DeleteEnvironmentAsync(Guid tenantId, Guid environmentId, CancellationToken ct)
    {
        if (environmentId == tenantId) throw new InvalidOperationException("Production is the workspace; delete the workspace instead.");
        using var rls = RlsScope.Tenant(tenantId);
        var environment = await tenancy.Environments.IgnoreTenantFilter()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == environmentId, ct);
        if (environment is null) return null;

        using var _ = scope.SuppressBulkCapture();
        var manifest = new Dictionary<string, object?>(StringComparer.Ordinal);
        var siteCount = await SweepEnvironmentAsync(tenantId, environmentId, manifest, ct);

        // What other environments hold of it: links to it, and deployments from it.
        foreach (var other in await TenancyPermissionResolver.EnvironmentIdsAsync(tenancy, tenantId, ct))
        {
            if (other == environmentId) continue;
            using var there = RlsScope.Environment(tenantId, other);
            manifest["tenancy.EnvironmentLinksToIt"] = (manifest.GetValueOrDefault("tenancy.EnvironmentLinksToIt") as int? ?? 0)
                + await tenancy.EnvironmentLinks.IgnoreTenantFilter()
                    .Where(l => l.TenantId == tenantId && l.TargetEnvironmentId == environmentId).ExecuteDeleteAsync(ct);
        }
        var siblings = await tenancy.Environments.IgnoreTenantFilter()
            .Where(e => e.TenantId == tenantId && e.PromotesFrom.Contains(environmentId)).ToListAsync(ct);
        foreach (var sibling in siblings)
        {
            sibling.PromotesFrom.Remove(environmentId);
            // A stage taken out of the pipeline closes the gap: what took from it takes from its
            // sources (development -> staging -> production loses staging, keeps the line).
            if (environment.Purpose != EnvironmentPurpose.Stage) continue;
            sibling.PromotesFrom = [.. sibling.PromotesFrom, .. environment.PromotesFrom.Where(s => s != sibling.Id && !sibling.PromotesFrom.Contains(s))];
        }
        tenancy.Environments.Remove(environment);
        await tenancy.SaveChangesAsync(ct);
        environmentStore.Evict(tenantId);

        var objects = 0;
        var storageFailed = false;
        try
        {
            objects = await storage.DeletePrefixAsync(storageOptions.Value.MediaBucket, $"tenants/{tenantId}/environments/{environmentId}/", ct);
        }
        catch (Exception ex)
        {
            storageFailed = true;
            logger.LogError(ex, "Failed deleting the plugin files of deleted environment {EnvironmentId}.", environmentId);
        }
        var realmDeleted = await DeleteRealmAsync(environmentId, ct);

        audit.Record(AuditActions.EnvironmentDeleted)
            .InTenant(tenantId)
            .For("environment", environmentId, environment.Name)
            .As(AuditCategory.Security, AuditSeverity.Critical)
            .With("rows", manifest)
            .With("sites", siteCount)
            .With("objects", objects)
            .With("realm_deleted", realmDeleted)
            .With("storage_failed", storageFailed);
        await audit.FlushAsync(ct);
        return new TenantDeletionResult(environmentId, siteCount, objects, false, storageFailed);
    }

    /// <summary>
    /// One environment's rows, deleted as that environment, sites first: that removes their
    /// repositories and artifacts. Returns how many sites went.
    /// </summary>
    private async Task<int> SweepEnvironmentAsync(Guid tenantId, Guid environmentId, Dictionary<string, object?> manifest, CancellationToken ct)
    {
        using var rls = RlsScope.Environment(tenantId, environmentId);
        var siteIds = await sites.Sites.IgnoreTenantFilter()
            .Where(s => s.TenantId == tenantId).Select(s => s.Id).ToListAsync(ct);
        foreach (var siteId in siteIds)
        {
            await siteDeleter.DeleteAsync(siteId, ct);
        }

        // Children before parents within each schema: these are ExecuteDelete calls
        // issued straight to the database, so EF's cascade ordering does not apply.
        // Each schema gets its own transaction — they are separate DbContexts and
        // cannot share one without enlisting a distributed transaction.
        await SweepAsync(cms, manifest, ct,
            ("cms.ContentVersions", () => cms.ContentVersions.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("cms.Outbox", () => cms.Outbox.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("cms.ScheduledPublishes", () => cms.ScheduledPublishes.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("cms.ContentItems", () => cms.ContentItems.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            // Plugin documents and Transit-encrypted plugin credentials (dcms.storage / dcms.secrets).
            ("cms.PluginData", () => cms.PluginData.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("cms.PluginSecrets", () => cms.PluginSecrets.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("cms.PluginInstances", () => cms.PluginInstances.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(forms, manifest, ct,
            ("forms.Submissions", () => forms.Submissions.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        // Dynamic Apps: every application's configuration history (ADR 0021). Changes before the
        // revisions they belong to, revisions before the app rows that point at them.
        await SweepAsync(apps, manifest, ct,
            ("apps.FlowRunSteps", () => apps.FlowRunSteps.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("apps.FlowRuns", () => apps.FlowRuns.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("apps.FlowSchedules", () => apps.FlowSchedules.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("apps.SiteViewDefaults", () => apps.SiteViewDefaults.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("apps.SiteViews", () => apps.SiteViews.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("apps.Outbox", () => apps.Outbox.Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("apps.UniqueKeys", () => apps.UniqueKeys.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("apps.RelationLinks", () => apps.RelationLinks.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("apps.Records", () => apps.Records.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("apps.Changes", () => apps.Changes.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("apps.Revisions", () => apps.Revisions.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("apps.Apps", () => apps.Apps.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        // Functions (ADR 0029): every instance's builds. The repositories go with the tenant's git org.
        await SweepAsync(functions, manifest, ct,
            ("functions.Deployments", () => functions.Deployments.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        // User Authentication (ADR 0022): the access policy over the environment's realm. Grants
        // before the roles they point at.
        await SweepAsync(userAuth, manifest, ct,
            ("userauth.Grants", () => userAuth.Grants.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("userauth.Roles", () => userAuth.Roles.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("userauth.Gates", () => userAuth.Gates.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("userauth.ApiRules", () => userAuth.ApiRules.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(search, manifest, ct,
            ("search.Documents", () => search.Documents.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(chat, manifest, ct,
            ("chat.Messages", () => chat.Messages.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("chat.Conversations", () => chat.Conversations.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        // BUG-04: Meta connections hold Transit-encrypted access/page tokens; purge them too.
        await SweepAsync(social, manifest, ct,
            ("social.MediaMap", () => social.MediaMap.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("social.SyncStates", () => social.SyncStates.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("social.OAuthStates", () => social.OAuthStates.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("social.Connections", () => social.Connections.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(visitors, manifest, ct,
            ("visitors.RefreshTokens", () => visitors.RefreshTokens.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("visitors.Accounts", () => visitors.Accounts.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(tenancy, manifest, ct,
            ("tenancy.TableViewShares", () => tenancy.TableViewShares.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.TableViewDefaults", () => tenancy.TableViewDefaults.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.TableViews", () => tenancy.TableViews.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.MemberRoles", () => tenancy.MemberRoles.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.TenantRolePermissions", () => tenancy.TenantRolePermissions.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.TenantRoles", () => tenancy.TenantRoles.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.Domains", () => tenancy.Domains.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.EnvironmentMigrations", () => tenancy.EnvironmentMigrations.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.EnvironmentBaselines", () => tenancy.EnvironmentBaselines.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.EnvironmentPolicies", () => tenancy.EnvironmentPolicies.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.EnvironmentLinks", () => tenancy.EnvironmentLinks.IgnoreTenantFilter().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));
        return siteIds.Count;
    }

    /// <summary>The environment's realm in identity (ADR 0022); best effort, a realm left behind holds no content.</summary>
    private async Task<bool> DeleteRealmAsync(Guid environmentId, CancellationToken ct)
    {
        try
        {
            return await realms.DeleteRealmAsync(environmentId, ct);
        }
        catch (Exception ex) when (ex is RealmAdminException or HttpRequestException)
        {
            logger.LogError(ex, "Failed deleting the realm of deleted environment {EnvironmentId}.", environmentId);
            return false;
        }
    }

    /// <summary>
    /// Runs one schema's deletes inside a single transaction, so a schema is either
    /// fully swept or untouched. Query filters are bypassed throughout: the ambient
    /// tenant is the one being removed, and filtering on an explicit TenantId is what
    /// makes the sweep auditable.
    /// </summary>
    /// <param name="manifest">
    /// Row counts by table, accumulated across every schema. This is the audit record: after
    /// the sweep the rows are gone and nothing else can say what was there.
    /// </param>
    private static async Task SweepAsync(
        DbContext context,
        Dictionary<string, object?> manifest,
        CancellationToken ct,
        params (string Table, Func<Task<int>> Delete)[] deletes)
    {
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        foreach (var (table, delete) in deletes)
        {
            var rows = await delete();
            if (rows > 0)
            {
                // Summed: each environment is swept on its own.
                manifest[table] = (manifest.GetValueOrDefault(table) as int? ?? 0) + rows;
            }
        }
        await tx.CommitAsync(ct);
    }
}
