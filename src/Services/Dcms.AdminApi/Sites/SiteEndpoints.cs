using System.Text.Json;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Sites;

public static class SiteEndpoints
{
    public static IEndpointRouteBuilder MapSiteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/sites", async (SitesDbContext db, CancellationToken ct) =>
        {
            var sites = await db.Sites
                .OrderByDescending(s => s.UpdatedAt)
                .Select(s => new
                {
                    id = s.Id,
                    name = s.Name,
                    renderMode = s.RenderMode.ToString(),
                    activeBuildId = s.ActiveBuildId,
                    updatedAt = s.UpdatedAt,
                })
                .ToListAsync(ct);
            return Results.Ok(sites);
        }).RequirePermission(PlatformPermissions.SiteEdit);

        app.MapPost("/api/admin/sites", async (
            CreateSiteRequest body, SitesDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (!tenant.HasTenant)
            {
                return Results.BadRequest(new { error = "Select a tenant first (X-Dcms-Tenant header required)." });
            }
            var mode = Enum.TryParse<SiteRenderMode>(body.RenderMode, ignoreCase: true, out var m)
                ? m : SiteRenderMode.StaticPrerender;
            var site = new Site
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                Name = body.Name,
                RenderMode = mode,
                DraftDefinitionJson = body.Definition ?? "{\"version\":1,\"theme\":{},\"pages\":[],\"nav\":[]}",
            };
            db.Sites.Add(site);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/sites/{site.Id}", new { id = site.Id });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        app.MapGet("/api/admin/sites/{id:guid}", async (Guid id, SitesDbContext db, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            return Results.Ok(new
            {
                id = site.Id,
                name = site.Name,
                renderMode = site.RenderMode.ToString(),
                activeBuildId = site.ActiveBuildId,
                definition = JsonDocument.Parse(site.DraftDefinitionJson).RootElement,
            });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        app.MapPut("/api/admin/sites/{id:guid}/definition", async (
            Guid id, JsonElement definition, SitesDbContext db, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            site.DraftDefinitionJson = definition.GetRawText();
            site.DefinitionVersion++;
            site.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.SiteEdit);

        app.MapPost("/api/admin/sites/{id:guid}/publish", async (
            Guid id, SitesDbContext db, ITenantContext tenant, IEventPublisher events, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            var tenantId = tenant.TenantId!.Value;
            var build = new SiteBuild
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SiteId = site.Id,
                Status = SiteBuildStatus.Queued,
                DefinitionSnapshotJson = site.DraftDefinitionJson,
            };
            build.ArtifactPrefix = $"{tenantId}/{site.Id}/{build.Id}";
            db.Builds.Add(build);
            await db.SaveChangesAsync(ct);

            await events.PublishAsync(Subjects.SitePublishRequested, new SitePublishRequested(
                Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, site.Id, build.Id, site.RenderMode.ToString()), ct);

            return Results.Accepted($"/api/admin/sites/{site.Id}/builds/{build.Id}", new { buildId = build.Id });
        }).RequirePermission(PlatformPermissions.SitePublish);

        // Link a verified domain to a site so site-host serves it there.
        app.MapPost("/api/admin/domains/{id:guid}/site", async (
            Guid id, LinkSiteRequest body, TenancyDbContext db, CancellationToken ct) =>
        {
            var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (domain is null)
            {
                return Results.NotFound();
            }
            domain.SiteId = body.SiteId;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.DomainsManage);

        return app;
    }

    private sealed record CreateSiteRequest(string Name, string? RenderMode, string? Definition);
    private sealed record LinkSiteRequest(Guid SiteId);
}
