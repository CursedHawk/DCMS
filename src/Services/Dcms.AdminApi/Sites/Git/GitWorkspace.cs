using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Data.Sites;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Sites.Git;

/// <summary>
/// The git side of one web-IDE workspace: a repository, its default branch, and each member's
/// working draft per branch. A Mode B site is one (its drafts are keyed by the site's id); a
/// plugin's repository is another (keyed by its <see cref="RepoBinding"/>'s id, ADR 0029). The
/// routes of both resolve their own owner and permissions, then hand the rest to
/// <see cref="GitWorkspaces"/>, so a site and a Functions repository behave the same in the IDE.
/// </summary>
/// <param name="Id">What drafts are keyed by (<see cref="SiteDraft.SiteId"/>).</param>
/// <param name="Announce">Tells whoever has the workspace open that a branch moved: (branch, sha, message, author, actor).</param>
/// <param name="AnnounceDraft">Tells the same account's other tabs that its draft moved; null where nobody listens.</param>
/// <param name="RefuseRelease">Whether a commit or merge onto <c>release</c> is refused here (a deployment-managed environment, a missing permission).</param>
public sealed record GitWorkspace(
    Guid Id,
    Guid TenantId,
    string? RepoFullName,
    string DefaultBranch,
    Func<string, string?, string?, string?, Guid?, CancellationToken, Task> Announce,
    Func<DraftUpdate, CancellationToken, Task>? AnnounceDraft = null,
    Func<CancellationToken, Task<IResult?>>? RefuseRelease = null)
{
    /// <summary>The effective branch of a request: the given one, else the workspace's default.</summary>
    public string Branch(string? branch) => string.IsNullOrWhiteSpace(branch) ? DefaultBranch : branch.Trim();
}

public sealed record SaveFilesRequest(int? BaseVersion, Dictionary<string, FileWrite>? Put, DeleteEntry[]? Delete, string? ClientId, string? Origin);
public sealed record FileWrite(string Content, string? BaseHash);
public sealed record DeleteEntry(string Path, string? BaseHash);
public sealed record GitRestoreRequest(string Sha);
public sealed record CommitRequest(string? Branch, string? TargetBranch, string? NewBranch, string Message, string? Description, string? Resolve);
public sealed record CreateBranchRequest(string Name, string? From);
public sealed record MergeRequest(string Head, string? Base, string? Strategy);
public sealed record ResolveMergeRequest(string Head, string? Base, Dictionary<string, string?>? Resolutions);

/// <summary>The IDE and source-control operations every <see cref="GitWorkspace"/> shares.</summary>
public static class GitWorkspaces
{
    /// <summary>
    /// The current user's working draft for a branch: the "being worked on" version autosaves
    /// flush into, decoupled from git (commits are explicit). A first open is seeded from the
    /// branch's HEAD (recording BaseSha for divergence detection at commit time), or from
    /// <paramref name="fallbackSeed"/> while there is no repository yet.
    /// </summary>
    public static async Task<IResult> LoadDraftAsync(
        GitWorkspace ws, string? branch, Func<Dictionary<string, string>>? fallbackSeed,
        SitesDbContext db, SiteGitService git, CurrentUser user, CancellationToken ct)
    {
        var userId = user.UserId ?? Guid.Empty;
        var b = ws.Branch(branch);
        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.SiteId == ws.Id && d.UserId == userId && d.Branch == b, ct);

        if (draft is null)
        {
            // Seed from git HEAD (or the fallback if the repo isn't ready).
            var headSha = ws.RepoFullName is not null ? await git.HeadShaAsync(ws.RepoFullName, b, ct) : null;
            var seeded = ws.RepoFullName is not null && headSha is not null
                ? await git.ReadFilesAsync(ws.RepoFullName, b, ct)
                : fallbackSeed?.Invoke() ?? new Dictionary<string, string>();
            // A freshly auto-initialised repo has only the placeholder README; treat that as
            // empty so the IDE seeds its starter instead of showing it.
            if (seeded.Count == 1 && seeded.ContainsKey("README.md")) seeded.Clear();
            draft = new SiteDraft
            {
                Id = Guid.NewGuid(),
                TenantId = ws.TenantId,
                SiteId = ws.Id,
                UserId = userId,
                Branch = b,
                DefinitionJson = SiteFileMap.Serialize(seeded),
                BaseSha = headSha,
                Version = 0,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Drafts.Add(draft);
            await db.SaveChangesAsync(ct);
        }
        else if (ws.RepoFullName is not null && draft.BaseSha is not null
                 && await git.HeadShaAsync(ws.RepoFullName, b, ct) is { } head && head != draft.BaseSha)
        {
            // The branch moved without this draft (a deployment, a colleague, a `git push`). A
            // draft with no edits of its own follows it; one with edits stays, and committing
            // reconciles them onto the new head.
            var based = await git.ReadFilesAsync(ws.RepoFullName, draft.BaseSha, ct);
            if (based.Count == 1 && based.ContainsKey("README.md")) based = new Dictionary<string, string>(); // seeded as empty, above
            if (SiteFileMap.Parse(draft.DefinitionJson) is var mine && mine.Count == based.Count
                && mine.All(f => based.TryGetValue(f.Key, out var v) && v == f.Value))
            {
                draft.DefinitionJson = SiteFileMap.Serialize(await git.ReadFilesAsync(ws.RepoFullName, head, ct));
                (draft.BaseSha, draft.Version, draft.UpdatedAt) = (head, draft.Version + 1, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
            }
        }

        var files = SiteFileMap.Parse(draft.DefinitionJson);
        return Results.Ok(new
        {
            branch = b,
            baseSha = draft.BaseSha,
            version = draft.Version,
            files,
            hashes = SiteFileMap.HashAll(files),
        });
    }

    /// <summary>
    /// Granular per-file save into the current user's working draft for a branch. Merges only
    /// the named files into the stored draft; each entry carries the hash the client last saw,
    /// so the same account editing the same file+branch from two tabs is reported as a conflict
    /// (409) instead of clobbering. Edits to different files just merge. No git write here.
    /// </summary>
    public static async Task<IResult> SaveFilesAsync(
        GitWorkspace ws, string? branch, SaveFilesRequest body, SitesDbContext db, CurrentUser user, CancellationToken ct)
    {
        var userId = user.UserId ?? Guid.Empty;
        var b = ws.Branch(branch);
        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.SiteId == ws.Id && d.UserId == userId && d.Branch == b, ct);
        if (draft is null)
            return Results.BadRequest(new { error = "Open the branch before saving." });

        var put = body.Put ?? new Dictionary<string, FileWrite>();
        var del = body.Delete ?? [];

        foreach (var path in put.Keys.Concat(del.Select(d => d.Path)))
        {
            // A workspace owns its whole project, so nothing is off-limits beyond path-safety.
            if (!SiteFileMap.IsSafePath(path))
                return Results.BadRequest(new { error = $"Illegal path in file map: {path}" });
        }

        var files = SiteFileMap.Parse(draft.DefinitionJson);

        var conflicts = new List<object>();
        foreach (var (path, write) in put)
        {
            var current = files.TryGetValue(path, out var c) ? SiteFileMap.Hash(c) : null;
            if (write.BaseHash != current)
                conflicts.Add(new { path, current = files.GetValueOrDefault(path) });
        }
        foreach (var d in del)
        {
            var current = files.TryGetValue(d.Path, out var c) ? SiteFileMap.Hash(c) : null;
            if (d.BaseHash != current)
                conflicts.Add(new { path = d.Path, current = files.GetValueOrDefault(d.Path) });
        }
        if (conflicts.Count > 0)
            return Results.Json(
                new { error = "conflict", version = draft.Version, conflicts },
                statusCode: StatusCodes.Status409Conflict);

        foreach (var (path, write) in put) files[path] = write.Content;
        foreach (var d in del) files.Remove(d.Path);

        draft.DefinitionJson = SiteFileMap.Serialize(files);
        draft.Version++;
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        /*
         * Announce the write, so a second tab (or the AI agent, which writes through this same
         * endpoint) learns the draft moved while the author is still typing rather than when
         * their next save is refused. The payload names the actor and the new version; the
         * client ignores anything that is not its own draft or is an echo of what it holds.
         */
        if (ws.AnnounceDraft is { } announce)
        {
            await announce(new DraftUpdate(
                SiteId: ws.Id,
                Branch: b,
                Version: draft.Version,
                Paths: [.. put.Keys, .. del.Select(d => d.Path)],
                ActorUserId: user.UserId,
                OccurredAt: DateTimeOffset.UtcNow,
                // Echoed so the writing tab can recognise its own save. Clamped: this is
                // client-supplied and goes straight back out over the hub.
                ClientId: Truncate(body.ClientId, 64)), ct);
        }

        return Results.Ok(new
        {
            version = draft.Version,
            hashes = put.ToDictionary(kv => kv.Key, kv => SiteFileMap.Hash(kv.Value.Content)),
        });
    }

    public static async Task<IResult> BranchesAsync(GitWorkspace ws, SiteGitService git, CancellationToken ct)
    {
        if (ws.RepoFullName is null) return Results.Ok(Array.Empty<object>());
        var branches = await git.BranchesAsync(ws.RepoFullName, ct);
        return Results.Ok(branches.Select(b => new { name = b.Name, sha = b.Commit?.Id }));
    }

    public static async Task<IResult> HistoryAsync(GitWorkspace ws, string? branch, int? limit, SiteGitService git, CancellationToken ct)
    {
        if (ws.RepoFullName is null) return Results.Ok(Array.Empty<object>());
        var commits = await git.HistoryAsync(ws.RepoFullName, ws.Branch(branch), Math.Clamp(limit ?? 50, 1, 100), ct);
        return Results.Ok(commits.Select(c => new
        {
            sha = c.Sha,
            shortSha = c.Sha.Length >= 7 ? c.Sha[..7] : c.Sha,
            message = c.Commit?.Message,
            author = c.Commit?.Author?.Name,
            date = c.Commit?.Author?.Date,
            htmlUrl = c.HtmlUrl,
            avatar = c.Author?.AvatarUrl,
        }));
    }

    /// <summary>
    /// Source-control "changes": the current user's working draft on a branch vs that branch's
    /// git HEAD. Content is included (both sides) so the Monaco diff needs no extra round-trip;
    /// oversized files return null content (marked truncated).
    /// </summary>
    public static async Task<IResult> ChangesAsync(
        GitWorkspace ws, string? branch, SitesDbContext db, CurrentUser user, SiteGitService git, CancellationToken ct)
    {
        if (ws.RepoFullName is null || !git.Enabled) return Results.Ok(Array.Empty<object>());

        var userId = user.UserId ?? Guid.Empty;
        var b = ws.Branch(branch);
        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.SiteId == ws.Id && d.UserId == userId && d.Branch == b, ct);
        var draftFiles = draft is null ? new Dictionary<string, string>() : SiteFileMap.Parse(draft.DefinitionJson);
        var headFiles = await git.ReadFilesAsync(ws.RepoFullName, b, ct);

        const int maxDiffBytes = 512 * 1024;
        static string? Cap(string? s) => s is not null && s.Length <= maxDiffBytes ? s : null;

        var changes = new List<object>();
        foreach (var path in draftFiles.Keys.Union(headFiles.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            var inHead = headFiles.TryGetValue(path, out var headContent);
            var inDraft = draftFiles.TryGetValue(path, out var draftContent);
            string status;
            if (inDraft && !inHead) status = "added";
            else if (!inDraft && inHead) status = "deleted";
            else if (headContent != draftContent) status = "modified";
            else continue; // unchanged
            changes.Add(new
            {
                path,
                status,
                headContent = Cap(headContent),
                draftContent = Cap(draftContent),
                truncated = (inHead && headContent!.Length > maxDiffBytes)
                            || (inDraft && draftContent!.Length > maxDiffBytes),
            });
        }
        return Results.Ok(changes);
    }

    /// <summary>
    /// Commits the current user's working draft (on <c>branch</c>) to that same branch, an
    /// existing <c>targetBranch</c>, or a fresh <c>newBranch</c>. The user is the git author; the
    /// platform is the committer. A commit to <c>release</c> fires the push webhook. Committing
    /// to the same branch after it moved underneath the draft replays the draft's edits onto the
    /// new head; a file changed on both sides is a 409 <c>branch-moved</c> unless
    /// <c>resolve</c> says whose version wins.
    /// </summary>
    public static async Task<IResult> CommitAsync(
        GitWorkspace ws, CommitRequest body, SitesDbContext db, CurrentUser user, SiteGitService git, CancellationToken ct)
    {
        if (!git.Enabled) return Results.Problem("Git backend is not configured.", statusCode: 503);
        if (string.IsNullOrWhiteSpace(body.Message))
            return Results.BadRequest(new { error = "A commit message is required." });
        if (ws.RepoFullName is null)
            return Results.BadRequest(new { error = "This workspace has no git repository." });

        var userId = user.UserId ?? Guid.Empty;
        var sourceBranch = ws.Branch(body.Branch);
        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.SiteId == ws.Id && d.UserId == userId && d.Branch == sourceBranch, ct);
        if (draft is null) return Results.BadRequest(new { error = "Nothing to commit — open the branch first." });

        var target = !string.IsNullOrWhiteSpace(body.NewBranch) ? body.NewBranch!.Trim()
            : string.IsNullOrWhiteSpace(body.TargetBranch) ? sourceBranch : body.TargetBranch!.Trim();
        // A release this environment leaves to deployments moves by a deployment only; the
        // platform commits as a git admin, past the branch's protection. Checked whichever way
        // the request names it: "creating" release reuses the branch there.
        if (target == SiteGitService.ReleaseBranch && ws.RefuseRelease is { } refuse && await refuse(ct) is { } refused)
        {
            return refused;
        }
        if (!string.IsNullOrWhiteSpace(body.NewBranch))
        {
            await git.CreateBranchAsync(ws.RepoFullName, target, sourceBranch, ct);
        }

        var draftFiles = SiteFileMap.Parse(draft.DefinitionJson);
        IReadOnlyDictionary<string, string> toCommit = draftFiles;

        if (target == sourceBranch && draft.BaseSha is not null)
        {
            var head = await git.HeadShaAsync(ws.RepoFullName, target, ct);
            if (head is not null && head != draft.BaseSha)
            {
                var reconcile = await git.ReconcileAsync(ws.RepoFullName, target, draft.BaseSha, draftFiles, ct);
                if (reconcile.Conflicts.Count > 0)
                {
                    var resolve = body.Resolve?.Trim().ToLowerInvariant();
                    if (resolve is not ("mine" or "theirs"))
                        return Results.Json(
                            new
                            {
                                error = "branch-moved",
                                head,
                                files = reconcile.Conflicts.Select(f => new
                                {
                                    path = f.Path,
                                    // ReleaseContent = the branch's current version; BranchContent = your edit.
                                    branchContent = f.ReleaseContent,
                                    draftContent = f.BranchContent,
                                    baseContent = f.BaseContent,
                                }),
                            },
                            statusCode: StatusCodes.Status409Conflict);

                    // "theirs" is already reflected in reconcile.Merged (it holds HEAD's version
                    // for conflicting files); "mine" overlays the draft's version.
                    var merged = new Dictionary<string, string>(reconcile.Merged, StringComparer.Ordinal);
                    if (resolve == "mine")
                        foreach (var f in reconcile.Conflicts)
                        {
                            if (f.BranchContent is null) merged.Remove(f.Path);
                            else merged[f.Path] = f.BranchContent;
                        }
                    toCommit = merged;
                }
                else
                {
                    toCommit = reconcile.Merged;
                }
            }
        }

        var message = string.IsNullOrWhiteSpace(body.Description)
            ? body.Message!.Trim()
            : body.Message!.Trim() + "\n\n" + body.Description!.Trim();
        var sha = await git.SyncAsync(ws.RepoFullName, target, toCommit, message, user.Name, user.Email, ct);

        if (target == sourceBranch)
        {
            // Draft content now equals the new HEAD (including anything a reconcile pulled in).
            draft.DefinitionJson = SiteFileMap.Serialize(toCommit);
            draft.BaseSha = sha;
        }
        else
        {
            // Work moved onto `target`; the source branch reverts to its (unchanged) HEAD so its
            // draft is clean again. The frontend switches to `target`.
            var srcHead = await git.HeadShaAsync(ws.RepoFullName, sourceBranch, ct);
            draft.DefinitionJson = SiteFileMap.Serialize(await git.ReadFilesAsync(ws.RepoFullName, sourceBranch, ct));
            draft.BaseSha = srcHead;
        }
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Everyone else with the workspace open is told the branch moved — most of all whoever
        // edits the SAME branch, whose draft is now based on a commit that is no longer HEAD.
        await ws.Announce(target, sha, body.Message?.Trim(), user.Name ?? user.Email, user.UserId, ct);

        return Results.Ok(new { sha, branch = target });
    }

    /// <summary>Creates a branch off another (defaults to the workspace's default branch).</summary>
    public static async Task<IResult> CreateBranchAsync(GitWorkspace ws, CreateBranchRequest body, SiteGitService git, CancellationToken ct)
    {
        if (!git.Enabled) return Results.Problem("Git backend is not configured.", statusCode: 503);
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new { error = "A branch name is required." });
        if (ws.RepoFullName is null)
            return Results.BadRequest(new { error = "This workspace has no git repository." });
        var from = ws.Branch(body.From);
        await git.CreateBranchAsync(ws.RepoFullName, body.Name!.Trim(), from, ct);
        return Results.Ok(new { name = body.Name!.Trim(), from });
    }

    /// <summary>Pre-merge review: what merging <c>head</c> into <c>base</c> (default release) would bring.</summary>
    public static async Task<IResult> CompareAsync(GitWorkspace ws, string head, string? @base, SiteGitService git, CancellationToken ct)
    {
        if (ws.RepoFullName is null || !git.Enabled || string.IsNullOrWhiteSpace(head))
            return Results.Ok(new { totalCommits = 0, files = Array.Empty<object>() });
        var b = string.IsNullOrWhiteSpace(@base) ? SiteGitService.ReleaseBranch : @base!.Trim();
        var cmp = await git.CompareAsync(ws.RepoFullName, b, head.Trim(), ct);
        return Results.Ok(new
        {
            totalCommits = cmp.TotalCommits,
            files = (cmp.Files ?? []).Select(f => new { path = f.Filename, status = f.Status }),
        });
    }

    /// <summary>
    /// Merges <c>head</c> into <c>base</c> (default release): a real 3-way merge, only true
    /// conflicts need resolution. A clean merge into release fires the push webhook.
    /// </summary>
    public static async Task<IResult> MergeAsync(GitWorkspace ws, MergeRequest body, CurrentUser user, SiteGitService git, CancellationToken ct)
    {
        if (!git.Enabled) return Results.Problem("Git backend is not configured.", statusCode: 503);
        if (string.IsNullOrWhiteSpace(body.Head))
            return Results.BadRequest(new { error = "A source branch is required." });
        if (ws.RepoFullName is null)
            return Results.BadRequest(new { error = "This workspace has no git repository." });

        var @base = string.IsNullOrWhiteSpace(body.Base) ? SiteGitService.ReleaseBranch : body.Base!.Trim();
        if (@base == SiteGitService.ReleaseBranch && ws.RefuseRelease is { } refuse && await refuse(ct) is { } refused)
            return refused;
        // Re-assert the webhook when shipping to release (recovers a repo whose initial hook
        // registration failed — otherwise the merge would never build).
        if (@base == SiteGitService.ReleaseBranch)
            try { await git.EnsureWebhookAsync(ws.RepoFullName, ct); } catch { /* best effort */ }

        var outcome = await git.MergeAsync(ws.RepoFullName, @base, body.Head!.Trim(), ct);
        if (outcome.Merged || outcome.UpToDate)
        {
            if (outcome.Merged)
                await ws.Announce(@base, outcome.Sha, $"Merge {body.Head!.Trim()} into {@base}", user.Name ?? user.Email, user.UserId, ct);
            return Results.Ok(new { merged = outcome.Merged, upToDate = outcome.UpToDate, sha = outcome.Sha });
        }
        return Results.Ok(new
        {
            merged = false,
            conflict = true,
            files = outcome.Conflicts.Select(f => new
            {
                path = f.Path,
                releaseContent = f.ReleaseContent,
                branchContent = f.BranchContent,
                baseContent = f.BaseContent,
            }),
        });
    }

    /// <summary>
    /// Completes a conflicted merge: the caller's per-file resolutions onto the auto-merged tree,
    /// committed as the two-parent merge. A null resolution deletes the file.
    /// </summary>
    public static async Task<IResult> ResolveMergeAsync(
        GitWorkspace ws, ResolveMergeRequest body, CurrentUser user, SiteGitService git, CancellationToken ct)
    {
        if (!git.Enabled) return Results.Problem("Git backend is not configured.", statusCode: 503);
        if (string.IsNullOrWhiteSpace(body.Head))
            return Results.BadRequest(new { error = "A source branch is required." });
        if (ws.RepoFullName is null)
            return Results.BadRequest(new { error = "This workspace has no git repository." });

        var @base = string.IsNullOrWhiteSpace(body.Base) ? SiteGitService.ReleaseBranch : body.Base!.Trim();
        if (@base == SiteGitService.ReleaseBranch && ws.RefuseRelease is { } refuse && await refuse(ct) is { } refused)
            return refused;
        var sha = await git.ResolveMergeAsync(
            ws.RepoFullName, @base, body.Head!.Trim(), body.Resolutions ?? new Dictionary<string, string?>(), user.Name, user.Email, ct);
        await ws.Announce(@base, sha, $"Merge {body.Head!.Trim()} into {@base}", user.Name ?? user.Email, user.UserId, ct);
        return Results.Ok(new { merged = true, sha });
    }

    /// <summary>
    /// Restores an earlier commit's tree into the current user's working draft on a branch. It
    /// does not commit: the user reviews the diff and commits explicitly.
    /// </summary>
    public static async Task<IResult> RestoreAsync(
        GitWorkspace ws, GitRestoreRequest body, string? branch, SitesDbContext db, CurrentUser user, SiteGitService git, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Sha))
            return Results.BadRequest(new { error = "A commit sha is required." });
        if (ws.RepoFullName is null || !git.Enabled)
            return Results.BadRequest(new { error = "This workspace has no git repository." });

        var userId = user.UserId ?? Guid.Empty;
        var b = ws.Branch(branch);
        var files = await git.ReadFilesAsync(ws.RepoFullName, body.Sha, ct);

        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.SiteId == ws.Id && d.UserId == userId && d.Branch == b, ct);
        if (draft is null)
        {
            draft = new SiteDraft
            {
                Id = Guid.NewGuid(),
                TenantId = ws.TenantId,
                SiteId = ws.Id,
                UserId = userId,
                Branch = b,
                BaseSha = await git.HeadShaAsync(ws.RepoFullName, b, ct),
            };
            db.Drafts.Add(draft);
        }
        draft.DefinitionJson = SiteFileMap.Serialize(files);
        draft.Version++;
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            branch = b,
            version = draft.Version,
            files,
            hashes = SiteFileMap.HashAll(files),
        });
    }

    /// <summary>
    /// Clamps a client-supplied string that is about to be broadcast over the hub: opaque to the
    /// server, compared only, but echoed to every watching connection, so never unbounded.
    /// </summary>
    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
