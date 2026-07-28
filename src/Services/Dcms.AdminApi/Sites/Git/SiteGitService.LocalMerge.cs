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
    /// <summary>Produce a genuine two-parent merge commit (release + <paramref name="head"/>)
    /// carrying the user-resolved tree, push it to release, and return its sha. Returns null
    /// (caller falls back) when git isn't usable; throws are caught by the caller.</summary>
    private async Task<string?> ResolveMergeWithGitAsync(
        string repoFullName, string head, IReadOnlyDictionary<string, string?> resolutions,
        string? authorName, string? authorEmail, CancellationToken ct)
    {
        if (!Enabled) return null;
        var (org, repo) = Split(repoFullName);
        await EnsureReleaseBranchAsync(repoFullName, ct);

        var work = Path.Combine(Path.GetTempPath(), "dcms-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var url = AuthenticatedCloneUrl(org, repo);
            // A normal (non-single-branch) clone fetches every branch tip, so origin/<head>
            // is present for the merge below.
            await RunGitAsync(work, null, ct, "clone", "--branch", ReleaseBranch, "--no-tags", url, ".");

            // Record <head> as a second parent without pulling in any of its content
            // (`-s ours`), then overlay the user's resolutions to reach the exact merged
            // tree. This can't itself conflict — the resolution already happened in the UI.
            await RunGitAsync(work, null, ct, "merge", "--no-ff", "--no-commit", "-s", "ours", "origin/" + head);

            ApplyResolutions(work, resolutions);
            await RunGitAsync(work, null, ct, "add", "-A");

            var env = CommitEnv(
                authorName ?? _opts.CommitterName, authorEmail ?? _opts.CommitterEmail,
                _opts.CommitterName, _opts.CommitterEmail);
            await RunGitAsync(work, env, ct, "commit", "-m", $"Merge {head} into {ReleaseBranch}");

            await RunGitAsync(work, null, ct, "push", "origin", "HEAD:" + ReleaseBranch);
            var sha = (await RunGitAsync(work, null, ct, "rev-parse", "HEAD")).Trim();
            return string.IsNullOrEmpty(sha) ? null : sha;
        }
        finally
        {
            TryDeleteDir(work);
        }
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
