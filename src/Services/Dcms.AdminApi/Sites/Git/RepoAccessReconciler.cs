using Dcms.Shared.Audit;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Sites.Git;

/// <summary>
/// Keeps a user's Forgejo repo access in step with their DCMS per-site repo
/// permissions (<c>repo:{siteId}:read|write</c>). For each Mode B site repo it
/// grants/updates/removes the user as a collaborator to match their effective
/// permissions. Idempotent and best-effort — a Forgejo failure logs and returns
/// (the next git open self-heals), never breaking the caller's request.
///
/// The user's Forgejo username is resolved from their email via the Forgejo API,
/// so there is no cross-service coupling to Identity's schema; if the account
/// hasn't been provisioned yet (user never logged in since credential sync), the
/// grant is skipped and re-applied on a later reconcile.
/// </summary>
public sealed class RepoAccessReconciler(
    ForgejoClient forgejo,
    SiteGitService git,
    TenancyPermissionResolver permissions,
    SitesDbContext sites,
    IAuditRecorder audit,
    ILogger<RepoAccessReconciler> logger)
{
    /// <summary>Reconcile the user's access to every Mode B site repo in the tenant.</summary>
    public async Task ReconcileUserAsync(
        Guid tenantId, Guid userId, string email, CancellationToken ct, bool isSuperAdmin = false)
    {
        if (!git.Enabled) return;
        try
        {
            var perms = await permissions.GetPermissionsAsync(tenantId, userId, ct);
            // A provisioned repo implies a git-backed render mode (A or B), so the
            // repo name is the only filter needed — and it stays correct if another
            // mode gains a repo later.
            var repos = await sites.Sites
                .Where(s => s.GitRepoFullName != null)
                .Select(s => new { s.Id, s.GitRepoFullName })
                .ToListAsync(ct);
            if (repos.Count == 0) return;

            var username = await forgejo.FindUsernameByEmailAsync(email, ct);
            if (username is null) return; // not provisioned yet; reconcile later

            foreach (var repo in repos)
            {
                await ApplyAsync(repo.GitRepoFullName!, repo.Id, username, perms, isSuperAdmin, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Repo-access reconcile (all sites) failed for user {UserId} in tenant {TenantId}.", userId, tenantId);

            // The permission change committed; the git access did not follow. Best-effort is
            // the right behaviour — a Forgejo outage must not fail the caller's request — but
            // silence is not: this is someone keeping push rights they were just denied, and
            // the next reconcile might be days away.
            audit.Record(AuditActions.GitAccessReconcileFailed)
                .InTenant(tenantId)
                .About(userId)
                .For("tenant", tenantId)
                .As(AuditCategory.Security, AuditSeverity.Warning)
                .Failed(ex.Message);
        }
    }

    /// <summary>Reconcile the user's access to a single site's repo (self-heal on git open).</summary>
    public async Task ReconcileUserSiteAsync(
        Guid tenantId, Guid userId, string email, Site site, CancellationToken ct, bool isSuperAdmin = false)
    {
        if (!git.Enabled || !site.RenderMode.IsGitBacked() || site.GitRepoFullName is null) return;
        try
        {
            var perms = await permissions.GetPermissionsAsync(tenantId, userId, ct);
            var username = await forgejo.FindUsernameByEmailAsync(email, ct);
            if (username is null) return;
            await ApplyAsync(site.GitRepoFullName, site.Id, username, perms, isSuperAdmin, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Repo-access reconcile failed for user {UserId} on site {SiteId}.", userId, site.Id);
        }
    }

    /// <summary>
    /// Desired Forgejo access for a user on one site repo. Site editors get push
    /// access to every Mode B repo: the permission "needed" to work on a site
    /// (<see cref="PlatformPermissions.SiteEdit"/> / <see cref="PlatformPermissions.SitePublish"/>,
    /// held by the Owner and admin roles) implies write on its source. SuperAdmins
    /// (who bypass tenant permission checks and thus hold no explicit grants) get
    /// write too. Explicit per-site <c>repo:{siteId}:read|write</c> grants still
    /// apply for finer-grained, non-editor access. Anything else → no collaborator.
    /// </summary>
    private async Task ApplyAsync(
        string repoFullName, Guid siteId, string username, IReadOnlySet<string> perms, bool isSuperAdmin, CancellationToken ct)
    {
        var (org, repo) = SplitRepo(repoFullName);
        var canWrite = isSuperAdmin
            || perms.Contains(PlatformPermissions.SiteEdit)
            || perms.Contains(PlatformPermissions.SitePublish)
            || perms.Contains(PlatformPermissions.RepoWrite(siteId));
        var desired = canWrite ? "write"
            : perms.Contains(PlatformPermissions.RepoRead(siteId)) ? "read"
            : null;

        if (desired is not null)
            await forgejo.AddCollaboratorAsync(org, repo, username, desired, ct);
        else
            await forgejo.RemoveCollaboratorAsync(org, repo, username, ct);
    }

    private static (string Org, string Repo) SplitRepo(string fullName)
    {
        var i = fullName.IndexOf('/');
        return i < 0 ? (fullName, "") : (fullName[..i], fullName[(i + 1)..]);
    }
}
