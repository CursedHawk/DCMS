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
using Dcms.Shared.Data.Forms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Search;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.Visitors;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
    SearchDbContext search,
    AnalyticsDbContext analytics,
    ChatDbContext chat,
    AiDbContext ai,
    VisitorsDbContext visitors,
    SiteDeleter siteDeleter,
    SiteGitService git,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    IAuditRecorder audit,
    AuditScope scope,
    ILogger<TenantDeleter> logger)
{
    public async Task<TenantDeletionResult?> DeleteAsync(Guid tenantId, CancellationToken ct)
    {
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

        // Sites first: this is what removes the Forgejo repos and the per-site
        // artifacts, and it leaves the org empty so it can be deleted below.
        var siteIds = await sites.Sites.IgnoreQueryFilters()
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
            ("cms.ContentVersions", () => cms.ContentVersions.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("cms.Outbox", () => cms.Outbox.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("cms.ScheduledPublishes", () => cms.ScheduledPublishes.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("cms.ContentItems", () => cms.ContentItems.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("cms.PluginInstances", () => cms.PluginInstances.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(media, manifest, ct,
            ("media.Variants", () => media.Variants.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("media.Assets", () => media.Assets.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("media.Folders", () => media.Folders.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(forms, manifest, ct,
            ("forms.Submissions", () => forms.Submissions.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(search, manifest, ct,
            ("search.Documents", () => search.Documents.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(analytics, manifest, ct,
            ("analytics.Events", () => analytics.Events.Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("analytics.DailyRollups", () => analytics.DailyRollups.Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(chat, manifest, ct,
            ("chat.Messages", () => chat.Messages.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("chat.Conversations", () => chat.Conversations.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(ai, manifest, ct,
            ("ai.UserSettings", () => ai.UserSettings.Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("ai.Settings", () => ai.Settings.Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(visitors, manifest, ct,
            ("visitors.RefreshTokens", () => visitors.RefreshTokens.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("visitors.Accounts", () => visitors.Accounts.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)));

        await SweepAsync(tenancy, manifest, ct,
            ("tenancy.MemberRoles", () => tenancy.MemberRoles.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.TenantRolePermissions", () => tenancy.TenantRolePermissions.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.Memberships", () => tenancy.Memberships.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.TenantRoles", () => tenancy.TenantRoles.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.Invitations", () => tenancy.Invitations.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.Domains", () => tenancy.Domains.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct)),
            ("tenancy.Tenants", () => tenancy.Tenants.Where(t => t.Id == key).ExecuteDeleteAsync(ct)));

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

        logger.LogWarning("Tenant {TenantId} ({Slug}) deleted: {Sites} sites, {Objects} objects.",
            tenantId, slug, siteIds.Count, objects);

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
            .With("sites", siteIds.Count)
            .With("objects", objects)
            .With("git_org_deleted", orgDeleted)
            .With("storage_failed", storageFailed);
        await audit.FlushAsync(ct);

        return new TenantDeletionResult(tenantId, siteIds.Count, objects, orgDeleted, storageFailed);
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
                manifest[table] = rows;
            }
        }
        await tx.CommitAsync(ct);
    }
}
