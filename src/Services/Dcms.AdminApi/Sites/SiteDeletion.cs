using Dcms.AdminApi.Sites.Git;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Sites;

/// <summary>
/// What a site delete actually removed, so the caller can report partial success —
/// object storage and Forgejo are separate systems and can be down independently
/// of the database.
/// </summary>
public sealed record SiteDeletionResult(
    Guid SiteId,
    int BuildsDeleted,
    int DraftsDeleted,
    int DomainsUnlinked,
    int RolePermissionsRevoked,
    int ObjectsDeleted,
    bool RepoDeleted,
    bool StorageFailed);

/// <summary>
/// Deletes a site and everything that only exists because of it. Shared by the
/// site delete endpoint and (later) tenant deletion, so the cleanup steps live in
/// one place rather than being duplicated per caller.
///
/// Ordering is deliberate: the database rows go first, in one transaction, and the
/// external systems (object storage, Forgejo) are cleaned up afterwards on a
/// best-effort basis. A failure there leaks bytes or leaves an orphaned repo, both
/// of which are recoverable by hand; the reverse order risks a site row pointing at
/// artifacts that are already gone, which the serving path cannot recover from.
/// </summary>
public sealed class SiteDeleter(
    SitesDbContext sites,
    TenancyDbContext tenancy,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    SiteGitService git,
    ILogger<SiteDeleter> logger)
{
    public async Task<SiteDeletionResult?> DeleteAsync(Guid siteId, CancellationToken ct)
    {
        var site = await sites.Sites.FirstOrDefaultAsync(s => s.Id == siteId, ct);
        if (site is null)
        {
            return null;
        }

        var tenantId = site.TenantId;
        var repoFullName = site.GitRepoFullName;

        // --- Database ---
        // ExecuteDelete rather than load-then-Remove: a site can have thousands of
        // build rows, and there is nothing to validate per row.
        await using var tx = await sites.Database.BeginTransactionAsync(ct);
        var builds = await sites.Builds.Where(b => b.SiteId == siteId).ExecuteDeleteAsync(ct);
        var drafts = await sites.Drafts.Where(d => d.SiteId == siteId).ExecuteDeleteAsync(ct);
        sites.Sites.Remove(site);
        await sites.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Domains live in the tenancy schema (a different DbContext, so a different
        // transaction). Unlinking rather than deleting: the hostname is the tenant's
        // and stays theirs to point at another site.
        var domains = await tenancy.Domains.Where(d => d.SiteId == siteId).ToListAsync(ct);
        foreach (var domain in domains)
        {
            domain.SiteId = null;
            domain.IsPrimary = false;
        }

        // Per-site repo grants (repo:{siteId}:read|write) would otherwise linger in
        // roles as keys the permission catalog no longer lists — invisible in the UI
        // and impossible to remove from it.
        var readKey = PlatformPermissions.RepoRead(siteId);
        var writeKey = PlatformPermissions.RepoWrite(siteId);
        var revoked = await tenancy.TenantRolePermissions
            .Where(p => p.Permission == readKey || p.Permission == writeKey)
            .ExecuteDeleteAsync(ct);
        await tenancy.SaveChangesAsync(ct);

        // --- External systems (best effort) ---
        var prefix = $"{tenantId}/{siteId}/";
        var objects = 0;
        var storageFailed = false;
        foreach (var bucket in new[] { storageOptions.Value.SitesBucket, storageOptions.Value.BuildLogsBucket })
        {
            try
            {
                objects += await storage.DeletePrefixAsync(bucket, prefix, ct);
            }
            catch (Exception ex)
            {
                storageFailed = true;
                logger.LogError(ex, "Failed deleting {Bucket}/{Prefix} for deleted site {SiteId}.",
                    bucket, prefix, siteId);
            }
        }

        var repoDeleted = false;
        if (!string.IsNullOrWhiteSpace(repoFullName))
        {
            repoDeleted = await git.DeleteRepoAsync(repoFullName!, ct);
        }

        return new SiteDeletionResult(
            siteId, builds, drafts, domains.Count, revoked, objects, repoDeleted, storageFailed);
    }
}

public static class SiteDeletionEndpoints
{
    public static IEndpointRouteBuilder MapSiteDeletion(this IEndpointRouteBuilder app)
    {
        // Deleting a site destroys its build history and its git repo, so it needs
        // the strongest site permission rather than the everyday editing one.
        app.MapDelete("/api/admin/sites/{id:guid}", async (
            Guid id, SiteDeleter deleter, CancellationToken ct) =>
        {
            var result = await deleter.DeleteAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequirePermission(PlatformPermissions.SitePublish);

        return app;
    }
}
