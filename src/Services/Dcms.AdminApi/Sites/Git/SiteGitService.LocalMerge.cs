using System.Diagnostics;

namespace Dcms.AdminApi.Sites.Git;

/// <summary>
/// The one git operation the Forgejo REST API can't express: a real two-parent
/// merge commit when a feature branch conflicts with <c>release</c>. The Contents
/// API only makes single-parent commits, so a resolved merge would otherwise leave
/// the feature history detached from release. Here we drive the git CLI in a throwaway
/// clone to record the merge properly (release + head as parents), overlaying the
/// user's per-file resolutions, then push release (→ webhook build).
/// </summary>
public sealed partial class SiteGitService
{
    /// <summary>
    /// A real 3-way merge of <paramref name="head"/> into <paramref name="base"/> via the git
    /// CLI in a throwaway clone. Clean changes on either side auto-merge; only files that both
    /// sides changed to different content are conflicts. Two modes:
    /// <list type="bullet">
    /// <item><b>Detect</b> (<paramref name="resolutions"/> null): a clean merge is committed and
    /// pushed (returns its sha); a conflict returns the unmerged files with base/ours/theirs
    /// content and makes no commit.</item>
    /// <item><b>Resolve</b> (<paramref name="resolutions"/> given): re-runs the merge, overlays
    /// the caller's per-file resolutions (null = delete) onto the auto-merged tree, then commits
    /// the genuine two-parent merge and pushes.</item>
    /// </list>
    /// Returns null (caller may fall back) only when git is disabled.
    /// </summary>
    private async Task<GitMergeResult?> MergeViaGitAsync(
        string repoFullName, string @base, string head,
        IReadOnlyDictionary<string, string?>? resolutions,
        string? authorName, string? authorEmail, CancellationToken ct)
    {
        if (!Enabled) return null;
        var (org, repo) = Split(repoFullName);

        var work = Path.Combine(Path.GetTempPath(), "dcms-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var url = AuthenticatedCloneUrl(org, repo);
            // A normal (non-single-branch) clone fetches every branch tip, so origin/<head>
            // is present for the merge below.
            await RunGitAsync(work, null, ct, "clone", "--branch", @base, "--no-tags", url, ".");

            // Nothing to do if head is already contained in base.
            var ahead = (await RunGitAsync(work, null, ct, "rev-list", "--count", $"HEAD..origin/{head}")).Trim();
            if (ahead == "0") return new GitMergeResult(UpToDate: true, Sha: null, Conflicts: []);

            var env = CommitEnv(
                authorName ?? _opts.CommitterName, authorEmail ?? _opts.CommitterEmail,
                _opts.CommitterName, _opts.CommitterEmail);

            // Attempt the real merge. A conflict exits non-zero and leaves unmerged paths.
            var merge = await RunGitRawAsync(work, env, ct, "merge", "--no-ff", "--no-commit", "origin/" + head);
            var unmerged = (await RunGitAsync(work, null, ct, "diff", "--name-only", "--diff-filter=U"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (resolutions is null)
            {
                if (unmerged.Length > 0)
                {
                    var conflicts = new List<MergeFile>(unmerged.Length);
                    foreach (var path in unmerged)
                    {
                        conflicts.Add(new MergeFile(
                            path,
                            await ShowStageAsync(work, 2, path, ct),   // ours = base branch
                            await ShowStageAsync(work, 3, path, ct),   // theirs = head branch
                            await ShowStageAsync(work, 1, path, ct)));  // base = merge-base
                    }
                    return new GitMergeResult(UpToDate: false, Sha: null, Conflicts: conflicts);
                }
                if (merge.ExitCode != 0)
                {
                    // Failed for a reason other than content conflict (e.g. bad ref).
                    throw new InvalidOperationException($"git merge failed: {Redact(merge.Stderr)}");
                }
                // Clean merge: commit the two-parent merge and push.
            }
            else
            {
                // Resolve mode: overlay the user's decisions on top of the auto-merged tree.
                ApplyResolutions(work, resolutions);
            }

            await RunGitAsync(work, null, ct, "add", "-A");
            await RunGitAsync(work, env, ct, "commit", "-m", $"Merge {head} into {@base}");
            await RunGitAsync(work, null, ct, "push", "origin", "HEAD:" + @base);
            var sha = (await RunGitAsync(work, null, ct, "rev-parse", "HEAD")).Trim();
            return new GitMergeResult(UpToDate: false, Sha: string.IsNullOrEmpty(sha) ? null : sha, Conflicts: []);
        }
        finally
        {
            TryDeleteDir(work);
        }
    }

    /// <summary>Content of a conflicted file at a merge stage (1=base, 2=ours, 3=theirs),
    /// or null if the file is absent at that stage (add/add, delete/modify, …).</summary>
    private async Task<string?> ShowStageAsync(string workDir, int stage, string path, CancellationToken ct)
    {
        var result = await RunGitRawAsync(workDir, null, ct, "show", $":{stage}:{path}");
        return result.ExitCode == 0 ? result.Stdout : null;
    }

    /// <summary>Overlay per-file resolutions onto the working tree: write chosen content,
    /// or delete when the resolution is null. Paths are validated to stay inside the clone.</summary>
    private static void ApplyResolutions(string workDir, IReadOnlyDictionary<string, string?> resolutions)
    {
        foreach (var (path, content) in resolutions)
        {
            var rel = SafeRelative(path);
            if (rel is null) continue;
            var full = Path.Combine(workDir, rel);
            if (content is null)
            {
                if (File.Exists(full)) File.Delete(full);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
        }
    }

    /// <summary>Normalize a repo-relative path to a safe OS path, rejecting traversal,
    /// absolute and drive-qualified inputs (mirrors the backend's entry-path rules).</summary>
    private static string? SafeRelative(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Replace('\\', '/').Trim().TrimStart('/');
        var segments = new List<string>();
        foreach (var seg in value.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg is ".." || seg.Contains(':')) return null;
            segments.Add(seg);
        }
        return segments.Count == 0 ? null : string.Join(Path.DirectorySeparatorChar, segments);
    }

    private string AuthenticatedCloneUrl(string org, string repo)
    {
        // Forgejo accepts a bot token as the basic-auth username for git-over-HTTP.
        var uri = new Uri(_opts.BaseUrl);
        return $"{uri.Scheme}://{Uri.EscapeDataString(_opts.Token)}@{uri.Authority}/{org}/{repo}.git";
    }

    private static Dictionary<string, string> CommitEnv(
        string authorName, string authorEmail, string committerName, string committerEmail) => new()
    {
        ["GIT_AUTHOR_NAME"] = authorName,
        ["GIT_AUTHOR_EMAIL"] = authorEmail,
        ["GIT_COMMITTER_NAME"] = committerName,
        ["GIT_COMMITTER_EMAIL"] = committerEmail,
    };

    /// <summary>Run <c>git</c> in <paramref name="workDir"/> and return stdout. Non-zero exit
    /// throws with stderr (token redacted). Credential prompts are disabled so a bad token
    /// fails fast instead of hanging.</summary>
    private async Task<string> RunGitAsync(
        string workDir, IReadOnlyDictionary<string, string>? env, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {Redact(string.Join(' ', args))} failed ({proc.ExitCode}): {Redact(stderr)}");
        return stdout;
    }

    /// <summary>Like <see cref="RunGitAsync"/> but returns the exit code and both streams
    /// instead of throwing — for commands (merge, show a stage) whose non-zero exit is a
    /// normal, expected outcome the caller must inspect.</summary>
    private async Task<GitRun> RunGitRawAsync(
        string workDir, IReadOnlyDictionary<string, string>? env, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return new GitRun(proc.ExitCode, await stdoutTask, await stderrTask);
    }

    private readonly record struct GitRun(int ExitCode, string Stdout, string Stderr);

    private string Redact(string s) =>
        string.IsNullOrEmpty(_opts.Token) ? s : s.Replace(_opts.Token, "***");

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            // git marks pack files read-only; clear the attribute so the recursive delete
            // doesn't trip on them.
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Temp dir — a leaked clone is harmless and the OS reaps it.
        }
    }
}
