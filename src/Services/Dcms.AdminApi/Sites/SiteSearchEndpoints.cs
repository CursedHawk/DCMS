using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Sites;

/// <summary>
/// What search engines may see of a site (#648): the pages its sitemap lists, and its people's
/// say over them — hidden pages, extra URLs, and whether the site is indexed at all. site-host
/// applies a change at once (<see cref="SiteChangeNotice"/>); it travels with the site in deployments.
/// </summary>
public static class SiteSearchEndpoints
{
    /// <param name="EnvironmentKey">The site's key across environments: whether deployments manage it is asked by it.</param>
    public sealed record SearchView(SiteSearchSettings Settings, IReadOnlyList<SitemapPage>? Pages, string? SitemapUrl, string EnvironmentKey);

    public static IEndpointRouteBuilder MapSiteSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/sites/{id:guid}/search", async (
            Guid id, SitesDbContext db, TenancyDbContext tenancy, IObjectStorage storage,
            IOptions<StorageOptions> storageOptions, CancellationToken ct) =>
        {
            var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            var prefix = site.ActiveBuildId is { } buildId
                ? await db.Builds.AsNoTracking().Where(b => b.Id == buildId).Select(b => b.ArtifactPrefix).FirstOrDefaultAsync(ct)
                : null;
            var host = await tenancy.Domains.AsNoTracking()
                .Where(d => d.SiteId == id && d.Branch == null && d.VerifiedAt != null)
                .OrderByDescending(d => d.IsPrimary).ThenBy(d => d.CreatedAt)
                .Select(d => d.Hostname).FirstOrDefaultAsync(ct);
            return Results.Ok(new SearchView(
                SiteSearchSettings.Parse(site.SearchSettingsJson),
                prefix is null ? null : await PagesAsync(storage, storageOptions.Value.SitesBucket, prefix, ct),
                host is null ? null : $"https://{host}/sitemap.xml",
                site.KeyAcrossEnvironments));
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Publishing rights: it changes what the live site answers search engines, at once.
        app.MapPut("/api/admin/sites/{id:guid}/search", async (
            Guid id, SiteSearchSettings body, SitesDbContext db, IEventPublisher events, ILoggerFactory logs, CancellationToken ct) =>
        {
            var settings = body with { Exclude = body.Exclude ?? [], Include = body.Include ?? [] };
            if (SiteSearchSettings.Validate(settings) is { Count: > 0 } errors)
            {
                return Results.BadRequest(new { error = errors[0], errors });
            }
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            site.SearchSettingsJson = settings.ToJson();
            site.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await SiteChangeNotice.PublishAsync(events, site, logs.CreateLogger("Dcms.AdminApi.Sites"), ct);
            return Results.Ok(SiteSearchSettings.Parse(site.SearchSettingsJson));
        }).RequirePermission(PlatformPermissions.SitePublish).WithAudit(AuditActions.SiteSearchChanged, "site")
            .ManagedByDeployments("sites");

        return app;
    }

    /// <summary>The page list the build stored for its sitemap; null for a build from before there was one.</summary>
    private static async Task<IReadOnlyList<SitemapPage>?> PagesAsync(IObjectStorage storage, string bucket, string prefix, CancellationToken ct)
    {
        var key = $"{prefix}/{SitemapPages.Key}";
        if (!await storage.ExistsAsync(bucket, key, ct))
        {
            return null;
        }
        try
        {
            await using var stream = await storage.GetAsync(bucket, key, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            return SitemapPages.Parse(buffer.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
