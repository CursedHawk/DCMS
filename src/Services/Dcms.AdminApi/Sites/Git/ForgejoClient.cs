using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Sites.Git;

/// <summary>
/// Thin typed client over the Forgejo (Gitea-compatible) REST API v1. Covers just
/// the operations the site git integration needs: provisioning orgs/repos, reading
/// and committing file trees, branches, commit history, archive download for the
/// builder, and webhook registration. Auth is a machine token sent as
/// "Authorization: token &lt;token&gt;". JSON is snake_case, matching the API.
/// </summary>
public sealed class ForgejoClient(HttpClient http, IOptions<ForgejoOptions> options, ILogger<ForgejoClient> logger)
{
    private readonly ForgejoOptions _opts = options.Value;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---------- orgs ----------

    /// <summary>Create the org if it does not already exist (private visibility).</summary>
    public async Task EnsureOrgAsync(string org, CancellationToken ct)
    {
        using var head = await http.GetAsync($"/api/v1/orgs/{Uri.EscapeDataString(org)}", ct);
        if (head.StatusCode == HttpStatusCode.OK) return;
        if (head.StatusCode != HttpStatusCode.NotFound) await ThrowFor(head, ct);

        using var res = await http.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrgOption(org, org, "private"), Json, ct);
        // 201 created; 422 if it raced into existence between the check and create.
        if (res.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.UnprocessableEntity))
            await ThrowFor(res, ct);
    }

    // ---------- repos ----------

    public async Task<bool> RepoExistsAsync(string owner, string repo, CancellationToken ct)
    {
        using var res = await http.GetAsync($"/api/v1/repos/{owner}/{repo}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return false;
        if (res.StatusCode != HttpStatusCode.OK) await ThrowFor(res, ct);
        return true;
    }

    /// <summary>Create an auto-initialised repo in the org and return it. Idempotent-ish:
    /// callers should check <see cref="RepoExistsAsync"/> first for the fast path.</summary>
    public async Task<RepoInfo> CreateRepoAsync(string org, string repo, string defaultBranch, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"/api/v1/orgs/{Uri.EscapeDataString(org)}/repos",
            new CreateRepoOption(repo, Private: true, AutoInit: true, DefaultBranch: defaultBranch), Json, ct);
        if (res.StatusCode != HttpStatusCode.Created) await ThrowFor(res, ct);
        return (await res.Content.ReadFromJsonAsync<RepoInfo>(Json, ct))!;
    }

    // ---------- branches ----------

    public async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string owner, string repo, CancellationToken ct)
    {
        var list = await http.GetFromJsonAsync<List<BranchInfo>>(
            $"/api/v1/repos/{owner}/{repo}/branches?limit=100", Json, ct);
        return list ?? [];
    }

    /// <summary>Head commit sha of a branch, or null if the branch does not exist.</summary>
    public async Task<string?> GetBranchHeadAsync(string owner, string repo, string branch, CancellationToken ct)
    {
        using var res = await http.GetAsync(
            $"/api/v1/repos/{owner}/{repo}/branches/{Uri.EscapeDataString(branch)}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (res.StatusCode != HttpStatusCode.OK) await ThrowFor(res, ct);
        var info = await res.Content.ReadFromJsonAsync<BranchInfo>(Json, ct);
        return info?.Commit?.Id;
    }

    public async Task CreateBranchAsync(string owner, string repo, string newBranch, string fromBranch, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"/api/v1/repos/{owner}/{repo}/branches",
            new CreateBranchOption(newBranch, fromBranch), Json, ct);
        if (res.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.UnprocessableEntity))
            await ThrowFor(res, ct);
    }

    /// <summary>Blob sha of a single file at a ref, or null if it doesn't exist.</summary>
    public async Task<string?> GetFileShaAsync(string owner, string repo, string path, string reference, CancellationToken ct)
    {
        using var res = await http.GetAsync(
            $"/api/v1/repos/{owner}/{repo}/contents/{path}?ref={Uri.EscapeDataString(reference)}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (res.StatusCode != HttpStatusCode.OK) await ThrowFor(res, ct);
        var meta = await res.Content.ReadFromJsonAsync<ContentMeta>(Json, ct);
        return meta?.Sha;
    }

    // ---------- file tree ----------

    /// <summary>Read every blob at a ref into a path→content map. Walks the recursive
    /// git tree, then fetches each blob (base64) — used to hydrate the IDE / builder.</summary>
    public async Task<Dictionary<string, string>> ReadTreeAsync(string owner, string repo, string reference, CancellationToken ct)
    {
        var tree = await http.GetFromJsonAsync<GitTree>(
            $"/api/v1/repos/{owner}/{repo}/git/trees/{Uri.EscapeDataString(reference)}?recursive=true&per_page=1000",
            Json, ct);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tree?.Tree is null) return files;
        foreach (var entry in tree.Tree)
        {
            if (!string.Equals(entry.Type, "blob", StringComparison.Ordinal) || entry.Path is null) continue;
            var blob = await http.GetFromJsonAsync<GitBlob>(
                $"/api/v1/repos/{owner}/{repo}/git/blobs/{entry.Sha}", Json, ct);
            if (blob?.Content is null) continue;
            files[entry.Path] = blob.Encoding == "base64"
                ? Encoding.UTF8.GetString(Convert.FromBase64String(blob.Content))
                : blob.Content;
        }
        return files;
    }

    /// <summary>Apply a batch of create/update/delete operations as one commit on a
    /// branch, and return the resulting commit sha.</summary>
    public async Task<string> ChangeFilesAsync(
        string owner, string repo, string branch, string message,
        IReadOnlyList<FileChange> changes, string? authorName, string? authorEmail, CancellationToken ct)
    {
        var identity = new CommitIdentity(
            authorName ?? _opts.CommitterName, authorEmail ?? _opts.CommitterEmail);
        var body = new ChangeFilesOptions(
            branch, message, changes,
            Author: identity,
            Committer: new CommitIdentity(_opts.CommitterName, _opts.CommitterEmail));
        using var res = await http.PostAsJsonAsync($"/api/v1/repos/{owner}/{repo}/contents", body, Json, ct);
        if (res.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK)) await ThrowFor(res, ct);
        var result = await res.Content.ReadFromJsonAsync<FilesResponse>(Json, ct);
        return result?.Commit?.Sha ?? throw new InvalidOperationException("Forgejo change-files returned no commit sha.");
    }

    // ---------- history ----------

    public async Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(
        string owner, string repo, string branch, int limit, CancellationToken ct)
    {
        var list = await http.GetFromJsonAsync<List<CommitInfo>>(
            $"/api/v1/repos/{owner}/{repo}/commits?sha={Uri.EscapeDataString(branch)}&limit={limit}&stat=false",
            Json, ct);
        return list ?? [];
    }

    // ---------- compare / pull requests (merge) ----------

    /// <summary>Diff summary between two refs (files changed in <paramref name="head"/>
    /// relative to <paramref name="base"/>), for the pre-merge review.</summary>
    public async Task<CompareResult> CompareAsync(string owner, string repo, string @base, string head, CancellationToken ct)
    {
        // The `...` (three-dot) form compares head against the merge-base with base.
        var spec = $"{Uri.EscapeDataString(@base)}...{Uri.EscapeDataString(head)}";
        var res = await http.GetFromJsonAsync<CompareResult>(
            $"/api/v1/repos/{owner}/{repo}/compare/{spec}", Json, ct);
        return res ?? new CompareResult(0, []);
    }

    /// <summary>Open a pull request head → base and return it (with the computed
    /// mergeable flag). If one already exists (409), the open PR is returned instead.</summary>
    public async Task<PullInfo?> CreatePullAsync(
        string owner, string repo, string @base, string head, string title, string? body, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"/api/v1/repos/{owner}/{repo}/pulls",
            new CreatePullOption(@base, head, title, body), Json, ct);
        if (res.StatusCode == HttpStatusCode.Created)
            return await res.Content.ReadFromJsonAsync<PullInfo>(Json, ct);

        if (res.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            // A PR for this head→base is already open — reuse it.
            var open = await http.GetFromJsonAsync<List<PullInfo>>(
                $"/api/v1/repos/{owner}/{repo}/pulls?state=open&limit=50", Json, ct) ?? [];
            return open.FirstOrDefault(p =>
                string.Equals(p.Head?.Ref, head, StringComparison.Ordinal) &&
                string.Equals(p.Base?.Ref, @base, StringComparison.Ordinal));
        }
        await ThrowFor(res, ct);
        return null;
    }

    /// <summary>Fetch a pull request (its <c>mergeable</c> flag is computed lazily, so
    /// callers may need to re-read shortly after creation).</summary>
    public async Task<PullInfo?> GetPullAsync(string owner, string repo, long index, CancellationToken ct)
    {
        using var res = await http.GetAsync($"/api/v1/repos/{owner}/{repo}/pulls/{index}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (res.StatusCode != HttpStatusCode.OK) await ThrowFor(res, ct);
        return await res.Content.ReadFromJsonAsync<PullInfo>(Json, ct);
    }

    /// <summary>Merge a pull request with the given style (merge|squash|rebase). Returns
    /// true on success; false when the merge is blocked (conflict / not mergeable).</summary>
    public async Task<bool> MergePullAsync(string owner, string repo, long index, string style, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"/api/v1/repos/{owner}/{repo}/pulls/{index}/merge",
            new MergePullOption(style), Json, ct);
        if (res.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent)
            return true;
        if (res.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.MethodNotAllowed)
            return false;
        await ThrowFor(res, ct);
        return false;
    }

    public async Task ClosePullAsync(string owner, string repo, long index, CancellationToken ct)
    {
        using var res = await http.PatchAsJsonAsync($"/api/v1/repos/{owner}/{repo}/pulls/{index}",
            new EditPullOption("closed"), Json, ct);
        if (res.StatusCode != HttpStatusCode.Created && res.StatusCode != HttpStatusCode.OK)
        {
            // Best-effort cleanup — a failure to close a stale PR must not break the flow.
            logger.LogWarning("Failed to close PR #{Index} on {Repo}: {Status}", index, $"{owner}/{repo}", res.StatusCode);
        }
    }

    // ---------- archive (builder) ----------

    /// <summary>Download a ref as a gzipped tarball (repo contents), for the offline build.</summary>
    public async Task<byte[]> GetArchiveAsync(string owner, string repo, string reference, CancellationToken ct)
    {
        using var res = await http.GetAsync(
            $"/api/v1/repos/{owner}/{repo}/archive/{Uri.EscapeDataString(reference)}.tar.gz",
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode != HttpStatusCode.OK) await ThrowFor(res, ct);
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    // ---------- webhooks ----------

    public async Task EnsurePushWebhookAsync(string owner, string repo, string url, string secret, CancellationToken ct)
    {
        // Avoid duplicates: skip if a hook already targets this url.
        var existing = await http.GetFromJsonAsync<List<WebhookInfo>>(
            $"/api/v1/repos/{owner}/{repo}/hooks?limit=50", Json, ct) ?? [];
        if (existing.Any(h => h.Config is not null &&
                              h.Config.TryGetValue("url", out var u) && u == url))
            return;

        var body = new CreateHookOption(
            Type: "gitea", Active: true, Events: ["push"],
            Config: new Dictionary<string, string>
            {
                ["url"] = url,
                ["content_type"] = "json",
                ["secret"] = secret,
            });
        using var res = await http.PostAsJsonAsync($"/api/v1/repos/{owner}/{repo}/hooks", body, Json, ct);
        if (res.StatusCode != HttpStatusCode.Created) await ThrowFor(res, ct);
    }

    // ---------- users / collaborators (repo authorization) ----------

    /// <summary>Resolve a Forgejo username from an exact email match, or null if no
    /// account has that email yet (e.g. the user hasn't been provisioned/logged in).</summary>
    public async Task<string?> FindUsernameByEmailAsync(string email, CancellationToken ct)
    {
        var res = await http.GetFromJsonAsync<UserSearchResult>(
            $"/api/v1/users/search?q={Uri.EscapeDataString(email)}&limit=50", Json, ct);
        return res?.Data?.FirstOrDefault(u =>
            string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase))?.Login;
    }

    /// <summary>Grant a user collaborator access to a repo at the given permission
    /// ("read" | "write" | "admin"). Idempotent — re-adding updates the level.</summary>
    public async Task AddCollaboratorAsync(string owner, string repo, string username, string permission, CancellationToken ct)
    {
        using var res = await http.PutAsJsonAsync(
            $"/api/v1/repos/{owner}/{repo}/collaborators/{Uri.EscapeDataString(username)}",
            new AddCollaboratorOption(permission), Json, ct);
        if (res.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.Created or HttpStatusCode.OK))
            await ThrowFor(res, ct);
    }

    /// <summary>Revoke a user's collaborator access to a repo. A 404 (not a collaborator)
    /// is treated as success so reconciliation is idempotent.</summary>
    public async Task RemoveCollaboratorAsync(string owner, string repo, string username, CancellationToken ct)
    {
        using var res = await http.DeleteAsync(
            $"/api/v1/repos/{owner}/{repo}/collaborators/{Uri.EscapeDataString(username)}", ct);
        if (res.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotFound))
            await ThrowFor(res, ct);
    }

    /// <summary>List the current collaborator logins on a repo (excludes the owner org).</summary>
    public async Task<IReadOnlyList<string>> ListCollaboratorLoginsAsync(string owner, string repo, CancellationToken ct)
    {
        var list = await http.GetFromJsonAsync<List<UserInfo>>(
            $"/api/v1/repos/{owner}/{repo}/collaborators?limit=100", Json, ct);
        return list?.Select(u => u.Login).ToList() ?? [];
    }

    // ---------- clone URLs (for the IDE clone panel) ----------

    public string HttpCloneUrl(string owner, string repo) =>
        $"{_opts.PublicUrl.TrimEnd('/')}/{owner}/{repo}.git";

    public string SshCloneUrl(string owner, string repo) =>
        $"ssh://git@{_opts.SshHost}:{_opts.SshPort}/{owner}/{repo}.git";

    // ---------- helpers ----------

    private async Task ThrowFor(HttpResponseMessage res, CancellationToken ct)
    {
        var body = await res.Content.ReadAsStringAsync(ct);
        logger.LogError("Forgejo API {Method} {Uri} -> {Status}: {Body}",
            res.RequestMessage?.Method, res.RequestMessage?.RequestUri, (int)res.StatusCode, body);
        throw new ForgejoApiException(res.StatusCode, body);
    }

    // ---------- DTOs ----------

    private sealed record CreateOrgOption(string Username, string FullName, string Visibility);
    private sealed record CreateRepoOption(string Name, bool Private, bool AutoInit, string DefaultBranch);
    private sealed record CreateBranchOption(
        [property: JsonPropertyName("new_branch_name")] string NewBranchName,
        [property: JsonPropertyName("old_branch_name")] string OldBranchName);

    public sealed record RepoInfo(long Id, string Name, string FullName, string DefaultBranch, string CloneUrl, string SshUrl);
    public sealed record BranchInfo(string Name, BranchCommit? Commit);
    public sealed record BranchCommit(string Id);

    private sealed record ContentMeta(string? Sha);
    private sealed record GitTree(List<GitTreeEntry>? Tree, bool Truncated);
    private sealed record GitTreeEntry(string? Path, string? Type, string Sha);
    private sealed record GitBlob(string? Content, string? Encoding);

    public sealed record CommitInfo(string Sha, RepoCommit? Commit, string? HtmlUrl, CommitAuthor? Author);
    public sealed record RepoCommit(string Message, CommitUser? Author);
    public sealed record CommitUser(string Name, string Email, DateTimeOffset Date);
    public sealed record CommitAuthor(string? Login, string? AvatarUrl);

    public sealed record CompareResult(
        [property: JsonPropertyName("total_commits")] int TotalCommits,
        [property: JsonPropertyName("files")] List<CompareFile>? Files);
    public sealed record CompareFile(
        [property: JsonPropertyName("filename")] string Filename,
        [property: JsonPropertyName("status")] string? Status);

    private sealed record CreatePullOption(string Base, string Head, string Title, string? Body);
    private sealed record MergePullOption([property: JsonPropertyName("Do")] string Do);
    private sealed record EditPullOption(string State);
    public sealed record PullInfo(
        long Number, bool? Mergeable,
        [property: JsonPropertyName("head")] PullRef? Head,
        [property: JsonPropertyName("base")] PullRef? Base);
    public sealed record PullRef([property: JsonPropertyName("ref")] string? Ref);

    private sealed record ChangeFilesOptions(
        string Branch, string Message, IReadOnlyList<FileChange> Files,
        CommitIdentity Author, CommitIdentity Committer);
    private sealed record CommitIdentity(string Name, string Email);
    private sealed record FilesResponse(FileCommit? Commit);
    private sealed record FileCommit(string Sha);

    private sealed record WebhookInfo(long Id, Dictionary<string, string>? Config);
    private sealed record CreateHookOption(string Type, bool Active, string[] Events, Dictionary<string, string> Config);

    private sealed record UserSearchResult(List<UserInfo>? Data, bool Ok);
    private sealed record UserInfo(long Id, string Login, string? Email);
    private sealed record AddCollaboratorOption(
        [property: JsonPropertyName("permission")] string Permission);
}

/// <summary>A single file operation in a <see cref="ForgejoClient.ChangeFilesAsync"/> batch.</summary>
public sealed record FileChange(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("sha")] string? Sha)
{
    /// <summary>Create/update a text file (content is UTF-8, encoded base64 as the API requires).</summary>
    public static FileChange Put(string path, string content, string? sha) =>
        new(sha is null ? "create" : "update", path,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), sha);

    public static FileChange Delete(string path, string sha) => new("delete", path, null, sha);
}

public sealed class ForgejoApiException(HttpStatusCode status, string body)
    : Exception($"Forgejo API error {(int)status}: {body}")
{
    public HttpStatusCode Status { get; } = status;
}
