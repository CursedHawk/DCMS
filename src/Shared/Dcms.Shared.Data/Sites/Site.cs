namespace Dcms.Shared.Data.Sites;

public enum SiteRenderMode
{
    /// <summary>Mode A: editor/AI component tree prerendered to static HTML.</summary>
    StaticPrerender,

    /// <summary>Mode B: AI/editor-generated React app built to a static bundle.</summary>
    ReactApp,

    /// <summary>Mode C: user-uploaded, pre-built static files served as-is.</summary>
    StaticFiles,
}

public enum SiteBuildStatus
{
    Queued,
    Building,
    Succeeded,
    Failed,
}

/// <summary>
/// A tenant website. The draft definition (component tree) is authored in the
/// editor; publishing snapshots it into a build whose artifacts are served by
/// site-host once it becomes the active build.
/// </summary>
public sealed class Site : TenantEntity
{
    public string Name { get; set; } = string.Empty;
    public SiteRenderMode RenderMode { get; set; } = SiteRenderMode.StaticPrerender;

    /// <summary>The editable site definition (component tree) as JSON.</summary>
    public string DraftDefinitionJson { get; set; } = "{}";

    // --- StaticFiles mode (Mode C): the currently staged upload ready to publish. ---
    /// <summary>Object-storage key of the staged bundle zip, or null if nothing uploaded yet.</summary>
    public string? StaticBundleKey { get; set; }
    public string? StaticBundleName { get; set; }
    public long? StaticBundleSize { get; set; }
    public int? StaticBundleFileCount { get; set; }
    public DateTimeOffset? StaticBundleUploadedAt { get; set; }

    public Guid? ActiveBuildId { get; set; }
    public int DefinitionVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // --- Git-backed source (Mode B): mapping to the Forgejo repo that holds the
    // site's React source. Null until the repo has been provisioned. The IDE's
    // saves are mirrored as commits on GitDefaultBranch. ---
    public string? GitRepoFullName { get; set; }
    public string? GitDefaultBranch { get; set; }
    public DateTimeOffset? GitProvisionedAt { get; set; }

    public List<SiteBuild> Builds { get; set; } = [];
}

/// <summary>One publish attempt: an immutable snapshot of the definition and its rendered artifacts.</summary>
public sealed class SiteBuild : TenantEntity
{
    public Guid SiteId { get; set; }
    public SiteBuildStatus Status { get; set; } = SiteBuildStatus.Queued;
    public string DefinitionSnapshotJson { get; set; } = "{}";
    /// <summary>Git commit the build was sourced from (Mode B git-backed sites), for provenance/rollback.</summary>
    public string? GitCommitSha { get; set; }
    public string ArtifactPrefix { get; set; } = string.Empty;
    public string? LogObjectKey { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}
