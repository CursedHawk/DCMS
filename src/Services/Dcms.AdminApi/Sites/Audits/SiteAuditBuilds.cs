using System.Security.Cryptography;
using System.Text.Json;
using Dcms.AdminApi.Sites.Git;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Jobs;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Sites.Audits;

/// <summary>
/// An unpublished site, built to be audited (#649): what the person auditing sees in their editor —
/// their working draft of the branch, else the branch's head; for uploaded files, the staged upload —
/// built like any build but never made live, and served at <see cref="UrlFor"/> until it expires.
/// </summary>
public static class SiteAuditBuilds
{
    /// <summary>How long an audit build's address answers: long enough for both PSI runs, short enough to stay private.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>The address PageSpeed Insights is given: under the platform's managed zone, so its wildcard certificate covers it.</summary>
    public static string UrlFor(string token, IConfiguration config)
    {
        var zone = SiteBuild.ManagedZone(config["Domains:ManagedZone"]);
        return $"https://{SiteBuild.AuditHostPrefix}{token}.{zone}/";
    }

    /// <summary>Queues the build; the error to show instead when there is nothing to build.</summary>
    public static async Task<(SiteBuild? Build, string? Error)> QueueAsync(
        Site site, string? branch, Guid? userId, SitesDbContext db, CmsDbContext cms, IEventPublisher events,
        SiteGitService git, IJobQueue jobs, CancellationToken ct)
    {
        string snapshot;
        string? sha = null;
        if (site.RenderMode == SiteRenderMode.StaticFiles)
        {
            if (site.StaticBundleKey is null)
            {
                return (null, "Upload the site's files first: there is nothing to audit yet.");
            }
            snapshot = JsonSerializer.Serialize(new
            {
                bundleKey = site.StaticBundleKey,
                name = site.StaticBundleName,
                size = site.StaticBundleSize,
                fileCount = site.StaticBundleFileCount,
            });
        }
        else
        {
            var b = string.IsNullOrWhiteSpace(branch) ? site.GitDefaultBranch ?? SiteGitService.DefaultBranch : branch.Trim();
            var draft = userId is { } user
                ? await db.Drafts.AsNoTracking().Where(d => d.SiteId == site.Id && d.UserId == user && d.Branch == b)
                    .Select(d => d.DefinitionJson).FirstOrDefaultAsync(ct)
                : null;
            if (draft is not null)
            {
                snapshot = draft;
            }
            else if (site.GitRepoFullName is { } repo && git.Enabled)
            {
                sha = await git.HeadShaAsync(repo, b, ct);
                snapshot = SiteFileMap.Serialize(await git.ReadFilesAsync(repo, b, ct));
            }
            else
            {
                snapshot = site.DraftDefinitionJson;
            }
            if (SiteFileMap.Parse(snapshot).Count == 0)
            {
                return (null, "This site has no source yet: there is nothing to audit.");
            }
        }

        var build = new SiteBuild
        {
            Id = Guid.NewGuid(),
            TenantId = site.TenantId,
            SiteId = site.Id,
            Status = SiteBuildStatus.Queued,
            DefinitionSnapshotJson = snapshot,
            GitCommitSha = sha,
            AuditToken = NewToken(),
            AuditExpiresAt = DateTimeOffset.UtcNow + Lifetime,
        };
        build.ArtifactPrefix = $"{site.TenantId}/{site.Id}/{build.Id}";
        db.Builds.Add(build);
        await db.SaveChangesAsync(ct);

        // On the ledger under its own title, so site-builder's reports land, and say what it is.
        await jobs.EnqueueAsync(new JobSpec(
            "site.build", site.TenantId, null, "jobs.kinds.siteAuditBuild.title", new { site = site.Name },
            RequiredPermission: PlatformPermissions.SiteEdit, Id: build.Id, Cancelable: false, Lane: "sites",
            Links: [new JobLink(JobLinkRels.Source, $"/sites/{site.Id}", site.Name)]), ct);
        var mode = site.RenderMode.ToString();
        await events.PublishAsync(Subjects.SitePublishSubjectFor(mode), new SitePublishRequested(
            Guid.NewGuid(), DateTimeOffset.UtcNow, site.TenantId, site.Id, build.Id, mode,
            await SiteBuilds.AnalyticsEnabledAsync(cms, site.TenantId, ct)), ct, messageId: $"build:{build.Id}");
        return (build, null);
    }
}
