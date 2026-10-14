using System.Text.RegularExpressions;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;
using SiteGitService = Dcms.AdminApi.Sites.Git.SiteGitService;

namespace Dcms.AdminApi.Sites;

/// <summary>
/// A site's publishing branches (#277): <c>release</c>, which is the site, and the branches
/// added here, each built on every push and served on the domains linked to it — a developer's
/// <c>dev</c> on dev.example.com. Choosing which branches publish is publishing, so it takes
/// site:publish; after that a commit to the branch deploys it, as one to release does.
/// </summary>
public static partial class PublishBranchEndpoints
{
    public static IEndpointRouteBuilder MapPublishBranchEndpoints(this IEndpointRouteBuilder app)
    {
        // Release first, then the branches by name, each with its live build and domains.
        app.MapGet("/api/admin/sites/{id:guid}/publish-branches", async (
            Guid id, SitesDbContext db, TenancyDbContext tenancy, CancellationToken ct) =>
        {
            var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();

            var branches = await db.PublishBranches.AsNoTracking()
                .Where(p => p.SiteId == id).OrderBy(p => p.Branch).ToListAsync(ct);
            var domains = await tenancy.Domains.AsNoTracking()
                .Where(d => d.SiteId == id)
                .OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Hostname)
                .Select(d => new { d.Id, d.Hostname, d.Branch, verified = d.VerifiedAt != null, primary = d.IsPrimary })
                .ToListAsync(ct);

            object Row(string? key, Guid? activeBuildId) => new
            {
                branch = key ?? SiteGitService.ReleaseBranch,
                release = key is null,
                activeBuildId,
                domains = domains.Where(d => d.Branch == key)
                    .Select(d => new { id = d.Id, hostname = d.Hostname, d.verified, d.primary }),
            };
            return Results.Ok(
                new[] { Row(null, site.ActiveBuildId) }
                    .Concat(branches.Select(b => Row(b.Branch, b.ActiveBuildId))));
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Start publishing a branch: it must exist in the site's repository. Its head is built at
        // once, so a domain linked next has something to serve.
        app.MapPost("/api/admin/sites/{id:guid}/publish-branches", async (
            Guid id, AddPublishBranchRequest body, SitesDbContext db, CmsDbContext cms,
            IEventPublisher events, CurrentUser user, SiteGitService git, SiteLiveUpdates live,
            AuditScope scope, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null) return Results.NotFound();
            if (!site.RenderMode.IsGitBacked() || site.GitRepoFullName is null || !git.Enabled)
                return Results.BadRequest(new { error = "Only a site whose source is in git can publish branches." });

            var branch = body.Branch?.Trim() ?? "";
            if (branch == SiteGitService.ReleaseBranch)
                return Results.BadRequest(new { error = "release always publishes, to the site's own domains." });
            if (!ValidBranch(branch))
                return Results.BadRequest(new { error = "That is not a branch name." });
            if (await db.PublishBranches.AnyAsync(p => p.SiteId == id && p.Branch == branch, ct))
                return Results.Conflict(new { error = $"The branch {branch} already publishes." });
            if (await git.HeadShaAsync(site.GitRepoFullName, branch, ct) is null)
                return Results.BadRequest(new { error = $"The site's repository has no branch {branch}." });

            db.PublishBranches.Add(new SitePublishBranch { Id = Guid.NewGuid(), TenantId = site.TenantId, SiteId = id, Branch = branch });
            await db.SaveChangesAsync(ct);
            try { await git.EnsureWebhookAsync(site.GitRepoFullName, ct); } catch { /* best effort, as for release */ }

            var buildId = await SiteEndpoints.EnqueueBranchBuildAsync(db, cms, events, git, site, live, user.UserId, branch, ct);
            scope.Declared?.For("site", site.Id, site.Name).With("branch", branch).With("build_id", buildId);
            return Results.Created($"/api/admin/sites/{id}/publish-branches", new { branch, buildId });
        }).RequirePermission(PlatformPermissions.SitePublish).WithAudit(AuditActions.SitePublishBranchAdded, "site");

        // Stop publishing a branch. Its domains are unlinked, not deleted — they are the tenant's —
        // and its builds stay in the history.
        app.MapDelete("/api/admin/sites/{id:guid}/publish-branches/{**branch}", async (
            Guid id, string branch, SitesDbContext db, TenancyDbContext tenancy, AuditScope scope, CancellationToken ct) =>
        {
            var row = await db.PublishBranches.FirstOrDefaultAsync(p => p.SiteId == id && p.Branch == branch, ct);
            if (row is null) return Results.NotFound();

            var domains = await tenancy.Domains.Where(d => d.SiteId == id && d.Branch == branch).ToListAsync(ct);
            foreach (var domain in domains)
            {
                domain.SiteId = null;
                domain.Branch = null;
            }
            db.PublishBranches.Remove(row);
            scope.Declared?.For("site", id).With("branch", branch).With("domains_unlinked", domains.Count);
            await db.SaveChangesAsync(ct);
            await tenancy.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.SitePublish).WithAudit(AuditActions.SitePublishBranchRemoved, "site");

        return app;
    }

    /// <summary>A plain git branch name: no leading dash or slash, no "..", no ".lock" ending.</summary>
    internal static bool ValidBranch(string branch) =>
        branch.Length is > 0 and <= 200 && BranchPattern().IsMatch(branch)
        && !branch.Contains("..", StringComparison.Ordinal) && !branch.Contains("//", StringComparison.Ordinal)
        && !branch.EndsWith('/') && !branch.EndsWith(".lock", StringComparison.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._/-]*$")]
    private static partial Regex BranchPattern();

    private sealed record AddPublishBranchRequest(string? Branch);
}
