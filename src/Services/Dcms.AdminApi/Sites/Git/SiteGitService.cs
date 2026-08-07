using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Sites.Git;

/// <summary>
/// Site-level git operations over <see cref="ForgejoClient"/>: one Forgejo org per
/// tenant, one repo per site. Provisions repos, seeds the initial file map, maps
/// the IDE's save-delta to a single commit, and reads branches/history/trees. All
/// no-ops unless a machine token is configured (<see cref="Enabled"/>), so wiring
/// this into the site flow is safe before the git backend is fully provisioned.
/// </summary>
public sealed partial class SiteGitService(
    ForgejoClient forgejo, IOptions<ForgejoOptions> options, ILogger<SiteGitService> logger)
{
    private readonly ForgejoOptions _opts = options.Value;

    public bool Enabled => _opts.Enabled;

    public const string DefaultBranch = "main";

    /// <summary>The production branch. A push/merge here triggers the build+deploy
    /// (see the git webhook) — this is what "publish" lands on.</summary>
    public const string ReleaseBranch = "release";

    public string OrgFor(string tenantSlug) => _opts.OrgPrefix + Sanitize(tenantSlug);
    public string RepoFor(Guid siteId) => $"site-{siteId}";
    public string RepoFullName(string tenantSlug, Guid siteId) => $"{OrgFor(tenantSlug)}/{RepoFor(siteId)}";

    /// <summary>Ensure the tenant org and site repo exist, seeding <paramref name="files"/>
    /// as the initial commit when the repo is freshly created. Returns the repo mapping.</summary>
    public async Task<SiteRepoInfo> EnsureRepoAsync(
        string tenantSlug, Guid siteId, IReadOnlyDictionary<string, string> files,
        string? authorName, string? authorEmail, CancellationToken ct)
    {
        var org = OrgFor(tenantSlug);
        var repo = RepoFor(siteId);

        await forgejo.EnsureOrgAsync(org, ct);

        var created = false;
        if (!await forgejo.RepoExistsAsync(org, repo, ct))
        {
            await forgejo.CreateRepoAsync(org, repo, DefaultBranch, ct);
            created = true;
        }

        if (created && files.Count > 0)
        {
            await SeedAsync(org, repo, files, authorName, authorEmail, ct);
        }

        // Ensure the release (production) branch exists — a push here builds+deploys.
        await EnsureReleaseBranchAsync($"{org}/{repo}", ct);

        // Register the push webhook (build-on-push). Best-effort: a webhook failure
        // must not fail provisioning.
        try { await forgejo.EnsurePushWebhookAsync(org, repo, _opts.WebhookUrl, _opts.WebhookSecret, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Webhook registration failed for {Repo}", $"{org}/{repo}"); }

        var head = await forgejo.GetBranchHeadAsync(org, repo, DefaultBranch, ct);
        logger.LogInformation("Provisioned site repo {Repo} (head {Head})", $"{org}/{repo}", head);
        return new SiteRepoInfo(
            $"{org}/{repo}", DefaultBranch, head,
            forgejo.HttpCloneUrl(org, repo), forgejo.SshCloneUrl(org, repo));
    }

    /// <summary>Replace the auto-init README with the real file map in a single commit.</summary>
    private async Task SeedAsync(
        string org, string repo, IReadOnlyDictionary<string, string> files,
        string? authorName, string? authorEmail, CancellationToken ct)
    {
        var changes = new List<FileChange>(files.Count + 1);
        foreach (var (path, content) in files)
        {
            if (string.Equals(path, "README.md", StringComparison.OrdinalIgnoreCase)) continue;
            changes.Add(FileChange.Put(path, content, sha: null));
        }
        // CreateRepo(auto_init) leaves a README.md; drop it unless the site provides one.
        if (!files.Keys.Any(p => string.Equals(p, "README.md", StringComparison.OrdinalIgnoreCase)))
        {
            var readmeSha = await forgejo.GetFileShaAsync(org, repo, "README.md", DefaultBranch, ct);
            if (readmeSha is not null) changes.Add(FileChange.Delete("README.md", readmeSha));
        }
        if (changes.Count == 0) return;
        await forgejo.ChangeFilesAsync(org, repo, DefaultBranch, "Seed site source", changes, authorName, authorEmail, ct);
    }

    /// <summary>Mirror an IDE save-delta to a git commit, resolving the current blob
    /// sha for each changed path. Best-effort: never throws — a git-side failure logs
    /// and returns null so it can't break the primary DB save. Returns the commit sha.</summary>
    public async Task<string?> CommitDeltaAsync(
        string repoFullName, string branch, string message,
        IReadOnlyDictionary<string, string> puts, IReadOnlyCollection<string> deletes,
        string? authorName, string? authorEmail, CancellationToken ct)
    {
        if (!Enabled) return null;
        try
        {
            var (org, repo) = Split(repoFullName);
            var changes = new List<FileChange>(puts.Count + deletes.Count);
            foreach (var (path, content) in puts)
            {
                var sha = await forgejo.GetFileShaAsync(org, repo, path, branch, ct);
                changes.Add(FileChange.Put(path, content, sha));
            }
            foreach (var path in deletes)
            {
                var sha = await forgejo.GetFileShaAsync(org, repo, path, branch, ct);
                if (sha is not null) changes.Add(FileChange.Delete(path, sha));
            }
            if (changes.Count == 0) return null;
            return await forgejo.ChangeFilesAsync(org, repo, branch, message, changes, authorName, authorEmail, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Git mirror commit to {Repo}@{Branch} failed", repoFullName, branch);
            return null;
        }
    }

    /// <summary>Commit a create/update/delete batch to a branch; returns the new commit sha.</summary>
    public Task<string> CommitAsync(
        string repoFullName, string branch, string message, IReadOnlyList<FileChange> changes,
        string? authorName, string? authorEmail, CancellationToken ct)
    {
        var (org, repo) = Split(repoFullName);
        return forgejo.ChangeFilesAsync(org, repo, branch, message, changes, authorName, authorEmail, ct);
    }

    public Task<Dictionary<string, string>> ReadFilesAsync(string repoFullName, string reference, CancellationToken ct)
    {
        var (org, repo) = Split(repoFullName);
        return forgejo.ReadTreeAsync(org, repo, reference, ct);
    }

    /// <summary>Head commit sha of a branch (or null if missing).</summary>
    public Task<string?> HeadShaAsync(string repoFullName, string branch, CancellationToken ct)
    {
        var (org, repo) = Split(repoFullName);
        return forgejo.GetBranchHeadAsync(org, repo, branch, ct);
    }

    /// <summary>Make the branch's tree exactly match <paramref name="desired"/> in one commit
    /// (create/update changed files, delete removed ones) and return the resulting head sha.
    /// Used at publish to guarantee git reflects the DB draft even if a mirror commit was missed.</summary>
    public async Task<string?> SyncAsync(
        string repoFullName, string branch, IReadOnlyDictionary<string, string> desired,
        string message, string? authorName, string? authorEmail, CancellationToken ct)
    {
        if (!Enabled) return null;
        var (org, repo) = Split(repoFullName);
        var current = await forgejo.ReadTreeAsync(org, repo, branch, ct);

        var changes = new List<FileChange>();
        foreach (var (path, content) in desired)
        {
            if (!current.TryGetValue(path, out var existing) || existing != content)
            {
                var sha = await forgejo.GetFileShaAsync(org, repo, path, branch, ct);
                changes.Add(FileChange.Put(path, content, sha));
            }
        }
        foreach (var path in current.Keys)
        {
            if (!desired.ContainsKey(path))
            {
                var sha = await forgejo.GetFileShaAsync(org, repo, path, branch, ct);
                if (sha is not null) changes.Add(FileChange.Delete(path, sha));
            }
        }

        if (changes.Count == 0) return await forgejo.GetBranchHeadAsync(org, repo, branch, ct);
        return await forgejo.ChangeFilesAsync(org, repo, branch, message, changes, authorName, authorEmail, ct);
    }

    /// <summary>Register (idempotently) the push webhook that triggers builds.</summary>
    public Task EnsureWebhookAsync(string repoFullName, CancellationToken ct)
    {
        var (org, repo) = Split(repoFullName);
        return forgejo.EnsurePushWebhookAsync(org, repo, _opts.WebhookUrl, _opts.WebhookSecret, ct);
    }

    /// <summary>The git identity used for platform-made commits (to distinguish IDE/publish
    /// commits from external CLI pushes in the webhook).</summary>
    public string CommitterEmail => _opts.CommitterEmail;

    /// <summary>HTTP + SSH clone URLs for the IDE clone panel.</summary>
    public (string Http, string Ssh) CloneUrls(string repoFullName)
    {
        var (org, repo) = Split(repoFullName);
        return (forgejo.HttpCloneUrl(org, repo), forgejo.SshCloneUrl(org, repo));
    }

    public Task<IReadOnlyList<ForgejoClient.BranchInfo>> BranchesAsync(string repoFullName, CancellationToken ct)
    {
        var (org, repo) = Split(repoFullName);
        return forgejo.ListBranchesAsync(org, repo, ct);
    }

    /// <summary>Create <paramref name="newBranch"/> off <paramref name="fromBranch"/> (idempotent).</summary>
    public Task CreateBranchAsync(string repoFullName, string newBranch, string fromBranch, CancellationToken ct)
    {
        var (org, repo) = Split(repoFullName);
        return forgejo.CreateBranchAsync(org, repo, newBranch, fromBranch, ct);
    }

    /// <summary>Ensure the release branch exists (create it off the default branch if
    /// missing). Idempotent and best-effort — used to back-fill repos provisioned
    /// before the release-branch model existed. Returns the release HEAD sha (or null).</summary>
    public async Task<string?> EnsureReleaseBranchAsync(string repoFullName, CancellationToken ct)
    {
        if (!Enabled) return null;
        var (org, repo) = Split(repoFullName);
        var head = await forgejo.GetBranchHeadAsync(org, repo, ReleaseBranch, ct);
        if (head is not null) return head;
        try { await forgejo.CreateBranchAsync(org, repo, ReleaseBranch, DefaultBranch, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Release-branch ensure failed for {Repo}", repoFullName); }
        return await forgejo.GetBranchHeadAsync(org, repo, ReleaseBranch, ct);
    }

    public Task<IReadOnlyList<ForgejoClient.CommitInfo>> HistoryAsync(string repoFullName, string branch, int limit, CancellationToken ct)
    {
        var (org, repo) = Split(repoFullName);
        return forgejo.ListCommitsAsync(org, repo, branch, limit, ct);
    }

    // ---------- merge (feature → release) ----------

    /// <summary>Diff summary of <paramref name="head"/> against <paramref name="base"/>
    /// (files the merge would bring), for the pre-merge review.</summary>
    public Task<ForgejoClient.CompareResult> CompareAsync(string repoFullName, string @base, string head, CancellationToken ct)
    {
        var (org, repo) = Split(repoFullName);
        return forgejo.CompareAsync(org, repo, @base, head, ct);
    }

    /// <summary>Merge <paramref name="head"/> into <paramref name="base"/> with a real 3-way
    /// git merge. Clean changes on either side auto-merge; only files both sides changed to
    /// different content are conflicts. A clean merge advances <paramref name="base"/> (a push
    /// to <c>release</c> → build) and returns its sha; a conflict makes no commit and returns
    /// the unmerged files with base/ours/theirs content for resolution
    /// (<see cref="ResolveMergeAsync"/>).</summary>
    public async Task<MergeOutcome> MergeAsync(
        string repoFullName, string @base, string head, CancellationToken ct)
    {
        if (!Enabled) return new MergeOutcome(false, null, false, []);
        if (string.Equals(@base, ReleaseBranch, StringComparison.Ordinal))
            await EnsureReleaseBranchAsync(repoFullName, ct);

        var result = await MergeViaGitAsync(repoFullName, @base, head, resolutions: null, null, null, ct)
            ?? new GitMergeResult(false, null, []);
        if (result.UpToDate) return new MergeOutcome(false, await HeadShaAsync(repoFullName, @base, ct), true, []);
        if (result.Sha is not null) return new MergeOutcome(true, result.Sha, false, []);
        return new MergeOutcome(false, null, false, result.Conflicts);
    }

    /// <summary>Complete a conflicted merge of <paramref name="head"/> into
    /// <paramref name="base"/>: re-run the 3-way merge, overlay the caller's per-file
    /// resolutions (null content = delete) onto the auto-merged tree, and commit the genuine
    /// two-parent merge (→ build when base is <c>release</c>). Falls back to a single-parent
    /// tree-sync commit only if the git CLI is unusable.</summary>
    public async Task<string?> ResolveMergeAsync(
        string repoFullName, string @base, string head, IReadOnlyDictionary<string, string?> resolutions,
        string? authorName, string? authorEmail, CancellationToken ct)
    {
        try
        {
            var result = await MergeViaGitAsync(repoFullName, @base, head, resolutions, authorName, authorEmail, ct);
            if (result?.Sha is not null) return result.Sha;
            if (result?.UpToDate == true) return await HeadShaAsync(repoFullName, @base, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Two-parent merge via git CLI failed for {Repo} ({Head}→{Base}); falling back to tree-sync commit.",
                repoFullName, head, @base);
        }
        return await ResolveMergeCore(repoFullName, @base, head, resolutions, authorName, authorEmail, ct);
    }

    private async Task<string?> ResolveMergeCore(
        string repoFullName, string @base, string head, IReadOnlyDictionary<string, string?> resolutions,
        string? authorName, string? authorEmail, CancellationToken ct)
    {
        var (org, repo) = Split(repoFullName);
        // Best-effort fallback: start from the head tree (so head's changes are kept) and
        // overlay the resolutions, then sync onto base. Loses two-parent topology but keeps content.
        var result = await forgejo.ReadTreeAsync(org, repo, head, ct);
        foreach (var (path, content) in resolutions)
        {
            if (content is null) result.Remove(path);
            else result[path] = content;
        }
        return await SyncAsync(repoFullName, @base, result, $"Merge {head} into {@base} (resolved)", authorName, authorEmail, ct);
    }

    /// <summary>Replay a working draft onto a branch whose HEAD has moved past
    /// <paramref name="baseSha"/> (the sha the draft was based on), at file granularity —
    /// the same model as <see cref="MergeAsync"/>. Starting from the current HEAD tree:
    /// a file only the user changed is applied; a file only the branch changed is kept;
    /// a file changed on both sides to <b>different</b> content is a conflict (both making
    /// the same change is not). Returns the merged file map and any true conflicts, so a
    /// commit onto a moved branch succeeds whenever the edits don't overlap.</summary>
    public async Task<ReconcileResult> ReconcileAsync(
        string repoFullName, string branch, string baseSha,
        IReadOnlyDictionary<string, string> draft, CancellationToken ct)
    {
        var (org, repo) = Split(repoFullName);
        var baseTree = await forgejo.ReadTreeAsync(org, repo, baseSha, ct);
        var headTree = await forgejo.ReadTreeAsync(org, repo, branch, ct);

        var merged = new Dictionary<string, string>(headTree, StringComparer.Ordinal);
        var conflicts = new List<MergeFile>();

        var paths = baseTree.Keys
            .Union(headTree.Keys, StringComparer.Ordinal)
            .Union(draft.Keys, StringComparer.Ordinal);
        foreach (var path in paths)
        {
            baseTree.TryGetValue(path, out var b);
            headTree.TryGetValue(path, out var h);
            draft.TryGetValue(path, out var d);

            if (d == b) continue;              // user didn't touch it → keep HEAD's version
            if (h != b && h != d)              // both sides changed it, differently → conflict
            {
                conflicts.Add(new MergeFile(path, h, d, b));
                continue;
            }
            if (d is null) merged.Remove(path); // user deleted it (branch didn't touch it)
            else merged[path] = d;              // user's version is safe on top of HEAD
        }

        return new ReconcileResult(merged, conflicts);
    }

    private static (string Org, string Repo) Split(string fullName)
    {
        var i = fullName.IndexOf('/');
        return i < 0 ? (fullName, "") : (fullName[..i], fullName[(i + 1)..]);
    }

    // Forgejo org/repo names allow [A-Za-z0-9-_.]; collapse anything else from the
    // slug to a hyphen so the org name is always valid.
    private static string Sanitize(string s) => NonSlug().Replace(s, "-").Trim('-').ToLowerInvariant();

    [GeneratedRegex("[^A-Za-z0-9-_.]+")]
    private static partial Regex NonSlug();
}

/// <summary>Where a site's source lives in git.</summary>
public sealed record SiteRepoInfo(
    string RepoFullName, string DefaultBranch, string? HeadSha, string HttpCloneUrl, string SshCloneUrl);

/// <summary>Result of a merge attempt into the target (base) branch.</summary>
public sealed record MergeOutcome(bool Merged, string? Sha, bool UpToDate, IReadOnlyList<MergeFile> Conflicts);

/// <summary>Low-level result of the git-CLI merge (<see cref="SiteGitService.MergeViaGitAsync"/>).</summary>
internal sealed record GitMergeResult(bool UpToDate, string? Sha, IReadOnlyList<MergeFile> Conflicts);

/// <summary>A file in conflict, with each side's content (null = absent on that side):
/// <paramref name="ReleaseContent"/> = the base/target-branch version ("ours"),
/// <paramref name="BranchContent"/> = the incoming version ("theirs"),
/// <paramref name="BaseContent"/> = the common ancestor (for a 3-way view).</summary>
public sealed record MergeFile(string Path, string? ReleaseContent, string? BranchContent, string? BaseContent = null);

/// <summary>Result of replaying a working draft onto a moved branch
/// (<see cref="SiteGitService.ReconcileAsync"/>): the file map to commit, plus any files
/// changed on both sides that need manual resolution.</summary>
public sealed record ReconcileResult(IReadOnlyDictionary<string, string> Merged, IReadOnlyList<MergeFile> Conflicts);
