namespace Dcms.Shared.Data.Sites;

/// <summary>
/// A per-user, per-branch working copy of a Mode B site's file map — the "being
/// worked on" version that autosaves debounce into as the user edits in the web IDE.
/// It is deliberately <b>not</b> git: commits are explicit. Drafts are isolated by
/// (site, user, branch) so two users never clobber each other and each branch keeps
/// its own in-progress edits. <see cref="BaseSha"/> records the branch HEAD the draft
/// was loaded from, so a commit can detect that the branch moved underneath it.
/// </summary>
public sealed class SiteDraft : TenantEntity
{
    public Guid SiteId { get; set; }

    /// <summary>The editing user (<c>CurrentUser.UserId</c>). Each user has their own draft.</summary>
    public Guid UserId { get; set; }

    /// <summary>The branch this draft targets / will commit to.</summary>
    public string Branch { get; set; } = string.Empty;

    /// <summary>The working file map as JSON: <c>{ "files": { "src/App.tsx": "…" } }</c>.</summary>
    public string DefinitionJson { get; set; } = "{}";

    /// <summary>Branch HEAD sha the draft was loaded from; null before the first load.
    /// A commit whose target HEAD no longer equals this indicates the branch moved.</summary>
    public string? BaseSha { get; set; }

    /// <summary>Bumps on every save — the optimistic-concurrency baseline for the IDE.</summary>
    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
