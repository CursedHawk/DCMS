using Dcms.Shared.Data.Sites;
using Microsoft.AspNetCore.SignalR;

namespace Dcms.AdminApi.Sites;

/// <summary>A deployment's state, in the shape the IDE's Deployments panel already renders.</summary>
public sealed record BuildUpdate(
    Guid SiteId,
    Guid Id,
    string Status,
    string? GitCommitSha,
    string? ShortSha,
    string? Error,
    bool HasLog,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    bool Active,
    Guid? ActorUserId);

/// <summary>A commit that landed on a branch, so anyone editing that branch learns it moved.</summary>
public sealed record CommitUpdate(
    Guid SiteId,
    string Branch,
    string Sha,
    string ShortSha,
    string? Message,
    string? Author,
    Guid? ActorUserId,
    DateTimeOffset OccurredAt);

/// <summary>
/// Tells everyone with a site open that its deployments or its branches moved.
///
/// <para>This exists because the IDE could not see its own publish. The Deployments panel polls
/// only while it can already see an in-flight build, so after a merge — where the build row is
/// created asynchronously, by the push webhook, after the merge request has returned — there
/// was nothing in flight to poll for. The panel invalidated its query against a list that did
/// not yet contain the new build, found nothing running, stopped polling, and sat on a stale
/// view of a deployment that was happening right then.</para>
///
/// <para>And that was only the single-user half. Two people on the same site never learned
/// anything about each other at all: a colleague's publish, or their commit to the branch you
/// have open in the editor, was invisible until you reloaded.</para>
///
/// <para>Every push is best-effort and every failure is swallowed. These are hints on top of
/// state that is already committed and already readable over REST — losing one costs a refresh,
/// and letting one fail a publish would trade a real operation for a cosmetic one.</para>
/// </summary>
public interface ISiteLiveUpdates
{
    Task BuildChangedAsync(Guid tenantId, BuildUpdate build, CancellationToken ct = default);
    Task CommitPushedAsync(Guid tenantId, CommitUpdate commit, CancellationToken ct = default);

    /// <summary>Announces a build row exactly as it was just created (always <c>Queued</c>).</summary>
    Task BuildQueuedAsync(Guid tenantId, SiteBuild build, Guid? actorUserId, CancellationToken ct = default);
}

public sealed class SiteLiveUpdates(
    IHubContext<SiteHub> hub,
    ILogger<SiteLiveUpdates> logger) : ISiteLiveUpdates
{
    public Task BuildChangedAsync(Guid tenantId, BuildUpdate build, CancellationToken ct = default) =>
        SendAsync(tenantId, build.SiteId, "BuildChanged", build, ct);

    public Task CommitPushedAsync(Guid tenantId, CommitUpdate commit, CancellationToken ct = default) =>
        SendAsync(tenantId, commit.SiteId, "CommitPushed", commit, ct);

    public Task BuildQueuedAsync(Guid tenantId, SiteBuild build, Guid? actorUserId, CancellationToken ct = default) =>
        BuildChangedAsync(tenantId, new BuildUpdate(
            SiteId: build.SiteId,
            Id: build.Id,
            Status: build.Status.ToString(),
            GitCommitSha: build.GitCommitSha,
            ShortSha: Short(build.GitCommitSha),
            Error: null,
            HasLog: false,
            CreatedAt: build.CreatedAt,
            CompletedAt: null,
            // A build that has not run cannot be the live one, whatever the site row says.
            Active: false,
            ActorUserId: actorUserId), ct);

    public static string? Short(string? sha) => sha is { Length: >= 7 } ? sha[..7] : sha;

    private async Task SendAsync(Guid tenantId, Guid siteId, string method, object payload, CancellationToken ct)
    {
        try
        {
            await hub.Clients.Group(SiteHub.SiteGroup(tenantId, siteId)).SendAsync(method, payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Pushing {Method} for site {Site} failed.", method, siteId);
        }
    }
}
