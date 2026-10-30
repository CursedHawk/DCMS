using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.PluginSdk.Abstractions.Platform;

/// <param name="ReadPermission">The calling plugin's own action (<c>read</c>) or full key that may read the repository in the IDE.</param>
/// <param name="EditPermission">… edit, commit and branch it.</param>
/// <param name="PublishPermission">… merge into <c>release</c>, which is what a push builds from.</param>
/// <param name="Seed">The first commit's files, when the repository is created by this call.</param>
/// <param name="InstanceId">Required only outside an instance (a job or event handler); must be the caller's own instance.</param>
public sealed record RepoSetup(
    string ReadPermission,
    string EditPermission,
    string PublishPermission,
    IReadOnlyDictionary<string, string>? Seed = null,
    Guid? InstanceId = null);

/// <param name="Id">What the IDE addresses it by: <c>/api/admin/repos/{Id}</c>.</param>
public sealed record RepoInfo(Guid Id, string RepoFullName, string DefaultBranch, string HttpCloneUrl, string SshCloneUrl);

public sealed record RepoQuery(Guid? InstanceId = null);

/// <param name="Reference">A branch or a commit sha.</param>
public sealed record RepoFilesQuery(string Reference, Guid? InstanceId = null);

public sealed record RepoFiles(IReadOnlyDictionary<string, string> Files);

public sealed record RepoHeadQuery(string Branch, Guid? InstanceId = null);

public sealed record RepoHead(string? Sha);

/// <summary>
/// A git repository for one of the calling plugin's instances, one per environment, edited in
/// the platform's web IDE like a Mode B site (ADR 0029). The plugin decides what the files mean
/// and which of its permissions read, edit and publish them; the platform keeps the repository,
/// the drafts and the history, and tells the plugin when <c>release</c> moves
/// (<see cref="RepoPushed"/>).
///
/// <para><b>Available in admin-api only</b>, where git is: nothing on the public plane edits code.</para>
/// </summary>
[DcmsContract("dcms.repos", 1, Description = "A git repository per instance, edited in the web IDE (admin plane only).",
    Events = [typeof(RepoPushed)])]
public interface IPluginRepos
{
    /// <summary>Creates the instance's repository in its environment, or returns the existing one.</summary>
    [Operation(OpRisk.Safe)]
    Task<RepoInfo> EnsureAsync(RepoSetup input, CancellationToken ct);

    [Operation(OpRisk.Read)]
    Task<RepoInfo?> GetAsync(RepoQuery input, CancellationToken ct);

    [Operation(OpRisk.Read)]
    Task<RepoFiles> ReadFilesAsync(RepoFilesQuery input, CancellationToken ct);

    [Operation(OpRisk.Read)]
    Task<RepoHead> HeadAsync(RepoHeadQuery input, CancellationToken ct);
}

/// <summary>
/// A plugin's repository moved on <see cref="Branch"/>: a commit or merge in the IDE, or a plain
/// <c>git push</c>. Published by the platform for every bound repository; a plugin subscribes to
/// act on its own (<see cref="PluginId"/>). The pusher is not named: the webhook proves the push
/// came from git, not who made it.
/// </summary>
[ContractEvent("dcms.repo.pushed")]
public sealed record RepoPushed(Guid InstanceId, Guid EnvironmentId, string PluginId, string Branch, string? Sha) : IPluginEvent;
