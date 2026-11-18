using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dcms.AdminApi.Sites.Git;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data;

namespace Dcms.AdminApi.Sites;

/// <summary>
/// Queuing a site build: shared by the site endpoints (publish, redeploy, the git webhook) and
/// jobs-worker's build-finished consumer, which queues the latest-wins catch-up (BUG-01).
/// </summary>
public static class SiteBuilds
{
    /// <summary>
    /// Plugins that record what visitors do (<c>PluginManifest.TracksVisitors</c>), snapshotted
    /// by AddDcmsAdminPlane in both admin-api and jobs-worker: the registry is fixed for the life of
    /// the process. Null until then, which the consent check treats as "ask".
    /// </summary>
    public static IReadOnlyList<string>? VisitorTrackingPlugins { get; set; }

    /// <summary>Queue a build of the current tree of <c>release</c> (<paramref name="publishBranch"/>
    /// null) or of a publishing branch, and request it. Reaps any stale in-flight builds first so a
    /// wedged queue can't block the new one.</summary>
    public static async Task<Guid> EnqueueBranchBuildAsync(
        SitesDbContext db, CmsDbContext cms, IEventPublisher events,
        Dcms.AdminApi.Sites.Git.SiteGitService git, Site site, SiteLiveUpdates live,
        Guid? actorUserId, string? publishBranch, CancellationToken ct)
    {
        await ReapStaleBuildsAsync(db, site.Id, ct);
        var gitBranch = publishBranch ?? Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch;
        var head = await git.HeadShaAsync(site.GitRepoFullName!, gitBranch, ct);
        var files = await git.ReadFilesAsync(site.GitRepoFullName!, gitBranch, ct);
        var build = new SiteBuild
        {
            Id = Guid.NewGuid(),
            TenantId = site.TenantId,
            SiteId = site.Id,
            Branch = publishBranch,
            Status = SiteBuildStatus.Queued,
            DefinitionSnapshotJson = SiteFileMap.Serialize(files),
            GitCommitSha = head,
        };
        build.ArtifactPrefix = $"{site.TenantId}/{site.Id}/{build.Id}";
        db.Builds.Add(build);
        await db.SaveChangesAsync(ct);
        var releaseMode = site.RenderMode.ToString();
        await live.BuildRecordedAsync(site.TenantId, build, site.Name, ct);
        await events.PublishAsync(Subjects.SitePublishSubjectFor(releaseMode), new SitePublishRequested(
            Guid.NewGuid(), DateTimeOffset.UtcNow, site.TenantId, site.Id, build.Id,
            releaseMode, await AnalyticsEnabledAsync(cms, site.TenantId, ct)), ct,
            messageId: $"build:{build.Id}");
        await live.BuildQueuedAsync(site.TenantId, build, actorUserId, ct);
        return build.Id;
    }

    /// <summary>
    /// Latest-wins catch-up (BUG-01). The webhook coalesces: a push that lands while a build
    /// is already running is dropped, because its snapshot would only stack another build.
    /// Nothing else ever built that dropped commit, so the live site could stay on the older
    /// tree indefinitely though the IDE said "released". This runs at the far end of every
    /// build (success or failure), re-reads the <c>release</c> head, and — if it has moved
    /// past what the finished build compiled and nothing is already in flight — queues one
    /// catch-up build of the current head. Any number of pushes that overlapped the build
    /// collapse into that single build of the latest tree.
    ///
    /// <para>Swallows everything: this is a best-effort tail on a notification consumer, and a
    /// throw here would fail the terminal message and re-run a notification that already
    /// landed. A missed catch-up is recoverable with a manual redeploy; a duplicated
    /// "your site is live" is not.</para>
    /// </summary>
    public static async Task ContinueIfReleaseMovedAsync(
        IServiceProvider scope, Guid tenantId, Guid siteId, Guid completedBuildId, CancellationToken ct)
    {
        try
        {
            var git = scope.GetRequiredService<Dcms.AdminApi.Sites.Git.SiteGitService>();
            if (!git.Enabled) return;

            // Runs from a build's terminal event, with no ambient tenant; the event names it (ADR
            // 0015), and the site its environment (ADR 0028), whose builds and release these are.
            var db = scope.GetRequiredService<SitesDbContext>();
            Guid environmentId;
            using (RlsScope.Platform())
            {
                environmentId = await db.Sites.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.Id == siteId && s.TenantId == tenantId).Select(s => s.EnvironmentId).FirstOrDefaultAsync(ct);
            }
            using var rls = RlsScope.Environment(tenantId, environmentId == Guid.Empty ? tenantId : environmentId);
            var site = await db.Sites.IgnoreTenantFilter()
                .FirstOrDefaultAsync(s => s.Id == siteId && s.TenantId == tenantId, ct);
            if (site is null || !site.RenderMode.IsGitBacked() || site.GitRepoFullName is null) return;

            var built = await db.Builds.AsNoTracking().IgnoreTenantFilter()
                .Where(b => b.Id == completedBuildId)
                .Select(b => new { b.GitCommitSha, b.Branch })
                .FirstOrDefaultAsync(ct);
            var builtSha = built?.GitCommitSha;

            // The branch the finished build published (#277); one that stopped publishing since
            // has nothing to catch up.
            var publishBranch = built?.Branch;
            if (publishBranch is not null &&
                !await db.PublishBranches.IgnoreTenantFilter().AnyAsync(p => p.SiteId == site.Id && p.Branch == publishBranch, ct))
                return;

            var release = publishBranch ?? Dcms.AdminApi.Sites.Git.SiteGitService.ReleaseBranch;
            var head = await git.HeadShaAsync(site.GitRepoFullName, release, ct);
            if (string.IsNullOrEmpty(head) ||
                string.Equals(head, builtSha, StringComparison.OrdinalIgnoreCase))
                return;

            // Something is already building (a manual redeploy, or a redelivery of this same
            // terminal event that already queued the catch-up): let it be — its own completion
            // re-checks the head, so the chain still converges on the latest commit.
            var inFlight = await db.Builds.IgnoreTenantFilter().AnyAsync(
                b => b.SiteId == site.Id && b.Branch == publishBranch && b.AuditToken == null &&
                     (b.Status == SiteBuildStatus.Queued || b.Status == SiteBuildStatus.Building), ct);
            if (inFlight) return;

            var cms = scope.GetRequiredService<CmsDbContext>();
            var events = scope.GetRequiredService<IEventPublisher>();
            var live = scope.GetRequiredService<SiteLiveUpdates>();
            var buildId = await EnqueueBranchBuildAsync(db, cms, events, git, site, live, null, publishBranch, ct);

            scope.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SiteBuilds)).LogInformation(
                "{Branch} head {Head} landed while build {Prev} was running; queued catch-up build {Build} for site {Site}.",
                release, SiteLiveUpdates.Short(head), completedBuildId, buildId, site.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SiteBuilds))
                .LogWarning(ex, "Could not evaluate the release catch-up for site {Site}.", siteId);
        }
    }

    /// <summary>
    /// Whether this tenant records analytics, resolved here and put on the publish
    /// message.
    ///
    /// It is answered by the producer rather than by site-builder because
    /// site-builder cannot answer it: it connects as a least-privilege role that
    /// reaches the `sites` schema only, so its own read of
    /// `plugins.plugin_instances` failed with 42501 on every publish and silently
    /// assumed "enabled" — which meant a site with analytics switched off still
    /// shipped a cookie banner. Admin-api already holds this permission, so nothing
    /// has to be granted to move the question here.
    ///
    /// Cross-tenant callers exist (the git webhook is anonymous and has no ambient
    /// tenant), so the query filter is bypassed and the tenant matched explicitly.
    /// A failure still errs towards asking rather than tracking.
    /// </summary>
    public static async Task<bool> AnalyticsEnabledAsync(CmsDbContext cms, Guid tenantId, CancellationToken ct)
    {
        try
        {
            if (VisitorTrackingPlugins is not { } tracking)
            {
                return true;
            }
            return await cms.PluginInstances.IgnoreTenantFilter().AsNoTracking()
                .AnyAsync(p => p.TenantId == tenantId && tracking.Contains(p.PluginId) && p.Enabled, ct);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// How long a build may sit in Queued/Building before a later publish declares it dead.
    ///
    /// <para>This has to exceed what a build is actually ALLOWED to take, and at 15 minutes it
    /// did not: <c>ReactAppBuilder</c> permits 10 minutes for the package install and 8 for
    /// the build itself, so a Mode B site on a cold dependency cache could be reaped while it
    /// was still running perfectly well. The result was a build that reported Failed and then
    /// Succeeded — the reaper's row, overwritten minutes later by the builder's own — which
    /// is worse than either outcome alone, because the author had already been told it broke.
    /// Reaping exists for a crashed builder or a lost message, not for a slow one.</para>
    /// </summary>
    private static readonly TimeSpan BuildStaleAfter = TimeSpan.FromMinutes(25);

    /// <summary>Mark builds stuck in Queued/Building past the timeout as Failed, so a crashed
    /// builder or lost message can't permanently block a site's publishing.</summary>
    public static async Task ReapStaleBuildsAsync(SitesDbContext db, Guid siteId, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - BuildStaleAfter;
        var stale = await db.Builds.IgnoreTenantFilter()
            .Where(b => b.SiteId == siteId
                && (b.Status == SiteBuildStatus.Queued || b.Status == SiteBuildStatus.Building)
                && b.CreatedAt < cutoff)
            .ToListAsync(ct);
        foreach (var b in stale)
        {
            b.Status = SiteBuildStatus.Failed;
            b.Error ??= $"Build timed out — no result within {BuildStaleAfter.TotalMinutes:0} minutes.";
            b.CompletedAt = DateTimeOffset.UtcNow;
        }
        if (stale.Count > 0) await db.SaveChangesAsync(ct);
    }
}
