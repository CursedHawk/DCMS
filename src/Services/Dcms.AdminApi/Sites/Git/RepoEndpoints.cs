using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Sites.Git;

/// <summary>
/// The web IDE's routes for a plugin's repository (ADR 0029): the same shapes as a site's
/// <c>/api/admin/sites/{id}/ide|git/…</c>, so the IDE only swaps its prefix, handled by the
/// same <see cref="GitWorkspaces"/>. What differs is who may: the repository's
/// <see cref="RepoBinding"/> names its owner's permissions — read to look, edit to change and
/// commit, publish to merge into <c>release</c>, which is what a push builds from.
/// </summary>
public static class RepoEndpoints
{
    private const string Resource = "repository";

    public static IEndpointRouteBuilder MapRepoEndpoints(this IEndpointRouteBuilder app)
    {
        var repo = app.MapGroup("/api/admin/repos/{id:guid}").RequireAuthorization()
            // Every route checks, in WithAsync, the permission the repository's binding names for
            // it: which one depends on the row, so the endpoint table cannot carry it.
            .PermissionExempt("Gated by the permission the repository's binding names (RepoEndpoints.WithAsync).");

        repo.MapGet("/git", (Guid id, HttpContext http, SitesDbContext db, SiteGitService git, IAuthorizationService authz, CancellationToken ct) =>
            WithAsync(id, Need.Read, http, db, authz, ct, (binding, _) =>
            {
                var (httpUrl, ssh) = git.CloneUrls(binding.RepoFullName);
                return Task.FromResult(Results.Ok(new
                {
                    enabled = git.Enabled,
                    provisioned = true,
                    repo = binding.RepoFullName,
                    branch = binding.DefaultBranch,
                    httpUrl,
                    sshUrl = ssh,
                    provisionedAt = binding.CreatedAt,
                }));
            }));

        repo.MapGet("/ide", (Guid id, string? branch, HttpContext http, SitesDbContext db, SiteGitService git, CurrentUser user,
                IAuthorizationService authz, CancellationToken ct) =>
            WithAsync(id, Need.Edit, http, db, authz, ct,
                (_, ws) => GitWorkspaces.LoadDraftAsync(ws, branch, fallbackSeed: null, db, git, user, ct)));

        repo.MapPatch("/ide/files", (Guid id, string? branch, SaveFilesRequest body, HttpContext http, SitesDbContext db, CurrentUser user,
                IAuthorizationService authz, CancellationToken ct) =>
            WithAsync(id, Need.Edit, http, db, authz, ct,
                (_, ws) => GitWorkspaces.SaveFilesAsync(ws, branch, body, db, user, ct)))
            .WithAudit(AuditActions.RepoFilesChanged, Resource);

        repo.MapGet("/git/branches", (Guid id, HttpContext http, SitesDbContext db, SiteGitService git, IAuthorizationService authz, CancellationToken ct) =>
            WithAsync(id, Need.Read, http, db, authz, ct, (_, ws) => GitWorkspaces.BranchesAsync(ws, git, ct)));

        repo.MapGet("/git/history", (Guid id, string? branch, int? limit, HttpContext http, SitesDbContext db, SiteGitService git,
                IAuthorizationService authz, CancellationToken ct) =>
            WithAsync(id, Need.Read, http, db, authz, ct, (_, ws) => GitWorkspaces.HistoryAsync(ws, branch, limit, git, ct)));

        repo.MapGet("/git/changes", (Guid id, string? branch, HttpContext http, SitesDbContext db, SiteGitService git, CurrentUser user,
                IAuthorizationService authz, CancellationToken ct) =>
            WithAsync(id, Need.Edit, http, db, authz, ct, (_, ws) => GitWorkspaces.ChangesAsync(ws, branch, db, user, git, ct)));

        repo.MapGet("/git/compare", (Guid id, string head, string? @base, HttpContext http, SitesDbContext db, SiteGitService git,
                IAuthorizationService authz, CancellationToken ct) =>
            WithAsync(id, Need.Read, http, db, authz, ct, (_, ws) => GitWorkspaces.CompareAsync(ws, head, @base, git, ct)));

        repo.MapPost("/git/commit", (Guid id, CommitRequest body, HttpContext http, SitesDbContext db, SiteGitService git, CurrentUser user,
                IAuthorizationService authz, CancellationToken ct) =>
            // A commit straight onto release ships it, as a merge into release does: the workspace's
            // RefuseRelease asks for the publish permission on the branch the handler itself resolves.
            WithAsync(id, Need.Edit, http, db, authz, ct,
                (_, ws) => GitWorkspaces.CommitAsync(ws, body, db, user, git, ct)))
            .WithAudit(AuditActions.RepoCommitted, Resource);

        repo.MapPost("/git/branches", (Guid id, CreateBranchRequest body, HttpContext http, SitesDbContext db, SiteGitService git,
                IAuthorizationService authz, CancellationToken ct) =>
            WithAsync(id, Need.Edit, http, db, authz, ct, (_, ws) => GitWorkspaces.CreateBranchAsync(ws, body, git, ct)))
            .WithAudit(AuditActions.RepoBranchCreated, Resource);

        repo.MapPost("/git/merge", (Guid id, MergeRequest body, HttpContext http, SitesDbContext db, SiteGitService git, CurrentUser user,
                IAuthorizationService authz, CancellationToken ct) =>
            WithAsync(id, IsRelease(body.Base, emptyIsRelease: true) ? Need.Publish : Need.Edit, http, db, authz, ct,
                (_, ws) => GitWorkspaces.MergeAsync(ws, body, user, git, ct)))
            .WithAudit(AuditActions.RepoMerged, Resource);

        repo.MapPost("/git/merge/resolve", (Guid id, ResolveMergeRequest body, HttpContext http, SitesDbContext db, SiteGitService git,
                CurrentUser user, IAuthorizationService authz, CancellationToken ct) =>
            WithAsync(id, IsRelease(body.Base, emptyIsRelease: true) ? Need.Publish : Need.Edit, http, db, authz, ct,
                (_, ws) => GitWorkspaces.ResolveMergeAsync(ws, body, user, git, ct)))
            .WithAudit(AuditActions.RepoMergeResolved, Resource);

        repo.MapPost("/git/restore", (Guid id, GitRestoreRequest body, string? branch, HttpContext http, SitesDbContext db, SiteGitService git,
                CurrentUser user, IAuthorizationService authz, CancellationToken ct) =>
            WithAsync(id, Need.Edit, http, db, authz, ct, (_, ws) => GitWorkspaces.RestoreAsync(ws, body, branch, db, user, git, ct)))
            .WithAudit(AuditActions.RepoRestored, Resource);

        return app;
    }

    private enum Need
    {
        Read,
        Edit,
        Publish,
    }

    private static bool IsRelease(string? branch, bool emptyIsRelease = false) =>
        string.IsNullOrWhiteSpace(branch) ? emptyIsRelease : branch.Trim() == SiteGitService.ReleaseBranch;

    /// <summary>The binding in the request's tenant and environment, if the caller holds the permission it names for <paramref name="need"/>.</summary>
    private static async Task<IResult> WithAsync(
        Guid id, Need need, HttpContext http, SitesDbContext db, IAuthorizationService authz, CancellationToken ct,
        Func<RepoBinding, GitWorkspace, Task<IResult>> handle)
    {
        var binding = await db.RepoBindings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (binding is null)
        {
            return Results.NotFound();
        }
        var permission = need switch
        {
            Need.Read => binding.ReadPermission,
            Need.Edit => binding.EditPermission,
            _ => binding.PublishPermission,
        };
        if (!(await authz.AuthorizeAsync(http.User, null, new PermissionRequirement(permission))).Succeeded)
        {
            return Results.Forbid();
        }
        // No live channel for plugin repositories yet: the IDE learns of moves on its next load.
        var workspace = new GitWorkspace(binding.Id, binding.TenantId, binding.RepoFullName, binding.DefaultBranch,
            Announce: (_, _, _, _, _, _) => Task.CompletedTask,
            RefuseRelease: async _ =>
                (await authz.AuthorizeAsync(http.User, null, new PermissionRequirement(binding.PublishPermission))).Succeeded
                    ? null
                    : Results.Forbid());
        return await handle(binding, workspace);
    }
}
