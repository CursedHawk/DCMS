using System.Text.Json;
using Dcms.AdminApi.Sites;
using Dcms.AdminApi.Sites.Git;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Environments.Kinds;

/// <summary>
/// Sites whose source is in git, by their identity across environments (ADR 0026): name, kind,
/// and the commit their <c>release</c> branch holds. Applying puts that commit on the receiving
/// environment's repository — fetched from whichever environment of the tenant has it — and
/// the push builds and deploys it there as any push to release does. Domains, publishing
/// branches, builds and uploaded (static-files) sites are each environment's own.
/// </summary>
// rls: EnvironmentMigrator calls every kind inside RlsScope.Environment, the environment it works
// on; IgnoreTenantFilter drops only the tenant filter, so queries stay in that environment.
public sealed class SitesConfigKind(SitesDbContext db, TenancyDbContext tenancy, SiteGitService git, SiteDeleter deleter)
    : IEnvironmentConfigKind
{
    public string Kind => "sites";

    public string? Permission => PlatformPermissions.SitePublish;

    public int Order => 40;

    private sealed record Value(string Name, string RenderMode, string? Release);

    public async Task<IReadOnlyList<EnvironmentConfigItem>> ExportAsync(Guid tenantId, CancellationToken ct)
    {
        var sites = await db.Sites.IgnoreTenantFilter().AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.RenderMode != SiteRenderMode.StaticFiles)
            .ToListAsync(ct);
        var items = new List<EnvironmentConfigItem>(sites.Count);
        foreach (var site in sites)
        {
            var release = site.GitRepoFullName is { } repo && git.Enabled
                ? await git.HeadShaAsync(repo, SiteGitService.ReleaseBranch, ct)
                : null;
            items.Add(new EnvironmentConfigItem(site.KeyAcrossEnvironments, JsonSerializer.SerializeToElement(
                new Value(site.Name, site.RenderMode.ToString(), release))) { Label = site.Name });
        }
        return items;
    }

    public async Task ApplyAsync(Guid tenantId, string key, JsonElement? value, EnvironmentApplyContext context, CancellationToken ct)
    {
        var site = await FindAsync(tenantId, key, ct);
        if (value is null)
        {
            if (site is not null) await deleter.DeleteAsync(site.Id, ct);
            return;
        }

        var wanted = value.Value.Deserialize<Value>()!;
        if (!Enum.TryParse<SiteRenderMode>(wanted.RenderMode, out var mode) || !mode.IsGitBacked())
        {
            throw new EnvironmentConfigException($"site '{wanted.Name}' has no source to deploy");
        }
        var created = site is null;
        if (site is null)
        {
            site = new Site { Id = Guid.NewGuid(), TenantId = tenantId, EnvironmentKey = key, RenderMode = mode };
            db.Sites.Add(site);
        }
        (site.Name, site.RenderMode, site.UpdatedAt) = (wanted.Name, mode, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);

        if (wanted.Release is not { } sha || !git.Enabled) return;
        if (site.GitRepoFullName is null)
        {
            var slug = await tenancy.Tenants.AsNoTracking().Where(t => t.Id == tenantId.ToString()).Select(t => t.Identifier).FirstAsync(ct);
            var info = await git.EnsureRepoAsync(slug, site.Id, new Dictionary<string, string>(), null, null, ct);
            (site.GitRepoFullName, site.GitDefaultBranch, site.GitProvisionedAt) = (info.RepoFullName, info.DefaultBranch, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            if (await EnvironmentPolicies.IsManagedAsync(tenancy, tenantId, Kind, key, ct))
            {
                await git.LockReleaseAsync(site.GitRepoFullName, locked: true, ct);
            }
        }
        if (await git.HeadShaAsync(site.GitRepoFullName, SiteGitService.ReleaseBranch, ct) == sha) return;

        var sources = await SourcesAsync(tenantId, key, ct);
        if (!await git.TransplantAsync(sources, site.GitRepoFullName, sha, SiteGitService.ReleaseBranch, ct))
        {
            throw new EnvironmentConfigException($"no environment of this workspace has commit {sha[..Math.Min(sha.Length, 12)]} of site '{wanted.Name}' any more");
        }
        // A site that arrived here gets its code on the branch its editor opens as well.
        if (created)
        {
            await git.TransplantAsync(sources, site.GitRepoFullName, sha, site.GitDefaultBranch ?? SiteGitService.DefaultBranch, ct);
        }
    }

    private Task<Site?> FindAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var id = Guid.TryParse(key, out var g) ? g : Guid.Empty;
        return db.Sites.IgnoreTenantFilter()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && (s.EnvironmentKey == key || (s.EnvironmentKey == null && s.Id == id)), ct);
    }

    /// <summary>The same site's repositories in the tenant's other environments.</summary>
    private async Task<List<string>> SourcesAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var here = Dcms.Shared.Data.Rls.RlsScope.EnvironmentFor(tenantId) ?? tenantId;
        var id = Guid.TryParse(key, out var g) ? g : Guid.Empty;
        // rls: platform -- one site's repository name in each of this tenant's other environments,
        // which the deployment already reviewed; nothing else is read.
        using var rls = Dcms.Shared.Data.Rls.RlsScope.Platform();
        return await db.Sites.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.EnvironmentId != here && s.GitRepoFullName != null
                        && (s.EnvironmentKey == key || (s.EnvironmentKey == null && s.Id == id)))
            .Select(s => s.GitRepoFullName!)
            .ToListAsync(ct);
    }
}
