namespace Dcms.Shared.Data.Sites;

/// <summary>
/// A git repository a plugin instance owns (ADR 0029): the Functions plugin's code, edited in
/// the same web IDE as a Mode B site. Its id keys the IDE's drafts as a site's id does
/// (<see cref="SiteDraft.SiteId"/>), and it says which of the owner's permissions read, edit
/// and publish it, because the host serves the routes but the plugin owns the meaning.
/// One per instance and environment, so each environment has its own repository.
/// </summary>
public sealed class RepoBinding : EnvironmentEntity
{
    /// <summary>The plugin whose instance owns it.</summary>
    public string PluginId { get; set; } = string.Empty;

    public Guid InstanceId { get; set; }

    public string RepoFullName { get; set; } = string.Empty;

    public string DefaultBranch { get; set; } = "main";

    /// <summary>Full permission keys (<c>plugin:functions:read</c>).</summary>
    public string ReadPermission { get; set; } = string.Empty;
    public string EditPermission { get; set; } = string.Empty;

    /// <summary>Merging into <c>release</c>, which is what a push builds from.</summary>
    public string PublishPermission { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
