using System.Text.Json;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
                staticBundle = site.StaticBundleKey is null ? null : new
                {
                    name = site.StaticBundleName,
                    size = site.StaticBundleSize,
                    fileCount = site.StaticBundleFileCount,
                    uploadedAt = site.StaticBundleUploadedAt,
                },
            });
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Upload a pre-built static bundle (Mode C): one .zip and/or loose files
        // (incl. a folder upload carrying relative paths). The files are sanitized
        // and normalized into a staged bundle; publishing snapshots it into a build.
        app.MapPost("/api/admin/sites/{id:guid}/upload", async (
            Guid id, HttpRequest request, SitesDbContext db, ITenantContext tenant,
            IObjectStorage storage, IOptions<StorageOptions> storageOptions, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            if (site.RenderMode != SiteRenderMode.StaticFiles)
            {
                return Results.BadRequest(new { error = "This site is not in static-files mode." });
            }
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Expected a multipart file upload." });
            }

            // Raise the body-size ceiling for this endpoint only (default Kestrel is
            // ~30 MB). Must be set before the body is read.
            var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
            {
                sizeFeature.MaxRequestBodySize = StaticSiteFiles.MaxUploadBytes;
            }

            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count == 0)
            {
                return Results.BadRequest(new { error = "No files were uploaded." });
            }

            StaticBundleBuilder.Result bundle;
            try
            {
                bundle = await StaticBundleBuilder.BuildAsync(form.Files, ct);
            }
            catch (StaticBundleException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { error = "A .zip file could not be read." });
            }

            var tenantId = tenant.TenantId!.Value;
            var uploadId = Guid.NewGuid();
            var key = StorageKeys.SiteBundleStaging(tenantId, site.Id, uploadId);
            await using (var upload = new MemoryStream(bundle.ZipBytes))
            {
                await storage.PutAsync(storageOptions.Value.SitesBucket, key, upload, bundle.ZipBytes.Length, "application/zip", ct);
            }

            site.StaticBundleKey = key;
            site.StaticBundleName = bundle.RootName ?? form.Files[0].FileName;
            site.StaticBundleSize = bundle.TotalBytes;
            site.StaticBundleFileCount = bundle.FileCount;
            site.StaticBundleUploadedAt = DateTimeOffset.UtcNow;
            site.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                fileCount = bundle.FileCount,
                size = bundle.TotalBytes,
                name = site.StaticBundleName,
                hasIndex = bundle.HasIndex,
            });
        }).RequirePermission(PlatformPermissions.SiteEdit).DisableAntiforgery();

        // Build history for a site (status + which one is live), drives the rollback UI.
        app.MapGet("/api/admin/sites/{id:guid}/builds", async (Guid id, SitesDbContext db, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            var builds = await db.Builds
                .Where(b => b.SiteId == id)
                .OrderByDescending(b => b.CreatedAt)
                .Select(b => new
                {
                    id = b.Id,
                    status = b.Status.ToString(),
                    error = b.Error,
                    createdAt = b.CreatedAt,
                    completedAt = b.CompletedAt,
                    isActive = site.ActiveBuildId == b.Id,
                })
                .ToListAsync(ct);
            return Results.Ok(builds);
        }).RequirePermission(PlatformPermissions.SiteEdit);

        // Roll back / forward: re-activate a previously succeeded build. Its artifacts
        // still live under their own prefix, so this is a pointer switch.
        app.MapPost("/api/admin/sites/{id:guid}/builds/{buildId:guid}/activate", async (
            Guid id, Guid buildId, SitesDbContext db, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (site is null)
            {
                return Results.NotFound();
            }
            var build = await db.Builds.FirstOrDefaultAsync(b => b.Id == buildId && b.SiteId == id, ct);
            if (build is null)
            {
                return Results.NotFound();
            }
            if (build.Status != SiteBuildStatus.Succeeded)
            {
                return Results.BadRequest(new { error = "Only a succeeded build can be activated." });
            }
            site.ActiveBuildId = build.Id;
            site.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.SitePublish);

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
            // StaticFiles publishes the staged upload; the snapshot records which
            // bundle to extract so the build is self-contained (race-free rollback).
            string snapshot;
            if (site.RenderMode == SiteRenderMode.StaticFiles)
            {
                if (site.StaticBundleKey is null)
                {
                    return Results.BadRequest(new { error = "Upload your website files before publishing." });
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
                snapshot = site.DraftDefinitionJson;
            }

            var tenantId = tenant.TenantId!.Value;
            var build = new SiteBuild
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SiteId = site.Id,
                Status = SiteBuildStatus.Queued,
                DefinitionSnapshotJson = snapshot,
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
