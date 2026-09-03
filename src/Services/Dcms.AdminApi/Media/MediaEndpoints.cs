using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using System.Security.Cryptography;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Media;

public static class MediaEndpoints
{
    private const long MaxInlineBytes = 50 * 1024 * 1024;

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/media", async (
            IFormFile file, [FromForm] Guid? folderId, MediaIngestService ingest, MediaDbContext db,
            CurrentUser me, CancellationToken ct) =>
        {
            if (file.Length > MediaIngestService.MaxInlineBytes)
            {
                // Checked before buffering: the point of the limit is not to read 2 GB into
                // memory first and then object to its size.
                return Results.BadRequest(new { error = "File exceeds the 50 MB inline upload limit." });
            }

            // A folder id, if given, must be one of the tenant's own folders.
            if (folderId is { } fid && !await db.Folders.AnyAsync(f => f.Id == fid, ct))
            {
                return Results.BadRequest(new { error = "Unknown folder." });
            }

            // Streamed straight into the ingest service, which hashes it and hands it to MinIO
            // without ever materialising a byte[]. Buffering here (and passing .ToArray()) cost
            // two copies of a 50 MB upload in the request's working set for no benefit.
            await using var upload = file.OpenReadStream();

            var result = await ingest.IngestAsync(
                upload, file.Length, file.FileName, folderId, me.UserId, ct);

            if (!result.Ok) return Results.BadRequest(new { error = result.Error });

            // The response says Processing for anything with renditions to build, and that is
            // the whole contract: the bytes are stored, the work is queued, and the client
            // polls or waits for the media.processed notification rather than for this request.
            return Results.Created($"/api/admin/media/{result.AssetId}", new
            {
                id = result.AssetId,
                category = result.Category.ToString(),
                contentType = result.ContentType,
                status = result.Status.ToString(),
            });
        }).RequirePermission(PlatformPermissions.MediaWrite).DisableAntiforgery().WithAudit(AuditActions.MediaUploaded, "media_asset");

        app.MapGet("/api/admin/media", async (Guid? folderId, MediaDbContext db, CancellationToken ct) =>
        {
            var query = db.Assets.AsQueryable();
            // folderId omitted → whole library; folderId=<empty guid> → unfiled only;
            // otherwise the given folder. (The SPA uses Guid.Empty as the "root/unfiled" tab.)
            if (folderId is { } fid)
            {
                query = fid == Guid.Empty
                    ? query.Where(a => a.FolderId == null)
                    : query.Where(a => a.FolderId == fid);
            }

            var assets = await query
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new
                {
                    id = a.Id,
                    category = a.Category.ToString(),
                    fileName = a.FileName,
                    status = a.Status.ToString(),
                    sizeBytes = a.SizeBytes,
                    folderId = a.FolderId,
                    createdAt = a.CreatedAt,
                    // "Compressed" footprint: total bytes of derived renditions.
                    variantBytes = a.Variants.Sum(v => (long?)v.SizeBytes) ?? 0,
                    variantCount = a.Variants.Count,
                    width = a.Variants.OrderByDescending(v => v.Width).Select(v => v.Width).FirstOrDefault(),
                    height = a.Variants.OrderByDescending(v => v.Width).Select(v => v.Height).FirstOrDefault(),
                })
                .ToListAsync(ct);
            return Results.Ok(assets);
        }).RequirePermission(PlatformPermissions.MediaRead);

        // Aggregate storage footprint for the whole tenant (originals + renditions),
        // with a per-category breakdown for the usage panel.
        app.MapGet("/api/admin/media/usage", async (MediaDbContext db, CancellationToken ct) =>
        {
            var originalBytes = await db.Assets.SumAsync(a => (long?)a.SizeBytes, ct) ?? 0;
            var variantBytes = await db.Variants.SumAsync(v => (long?)v.SizeBytes, ct) ?? 0;
            var assetCount = await db.Assets.CountAsync(ct);
            var folderCount = await db.Folders.CountAsync(ct);

            var byCategory = await db.Assets
                .GroupBy(a => a.Category)
                .Select(g => new
                {
                    category = g.Key.ToString(),
                    count = g.Count(),
                    originalBytes = g.Sum(a => (long?)a.SizeBytes) ?? 0,
                })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                originalBytes,
                variantBytes,
                totalBytes = originalBytes + variantBytes,
                assetCount,
                folderCount,
                byCategory,
            });
        }).RequirePermission(PlatformPermissions.MediaRead);

        // ---- Folders ----------------------------------------------------------

        app.MapGet("/api/admin/media/folders", async (MediaDbContext db, CancellationToken ct) =>
        {
            var folders = await db.Folders
                .OrderBy(f => f.Name)
                .Select(f => new
                {
                    id = f.Id,
                    name = f.Name,
                    parentId = f.ParentId,
                    createdAt = f.CreatedAt,
                    assetCount = db.Assets.Count(a => a.FolderId == f.Id),
                })
                .ToListAsync(ct);
            return Results.Ok(folders);
        }).RequirePermission(PlatformPermissions.MediaRead);

        app.MapPost("/api/admin/media/folders", async (
            CreateFolderRequest body, MediaDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var name = body.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                return Results.BadRequest(new { error = "A folder name is required." });
            }
            if (name.Length > 200)
            {
                name = name[..200];
            }
            if (body.ParentId is { } pid && !await db.Folders.AnyAsync(f => f.Id == pid, ct))
            {
                return Results.BadRequest(new { error = "Unknown parent folder." });
            }

            var folder = new MediaFolder
            {
                Id = Guid.NewGuid(),
                Name = name,
                ParentId = body.ParentId,
                CreatedBy = me.UserId,
            };
            db.Folders.Add(folder);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id = folder.Id, name = folder.Name, parentId = folder.ParentId, createdAt = folder.CreatedAt, assetCount = 0 });
        }).RequirePermission(PlatformPermissions.MediaWrite).WithAudit(AuditActions.MediaFolderCreated, "media_folder");

        app.MapPatch("/api/admin/media/folders/{id:guid}", async (
            Guid id, UpdateFolderRequest body, MediaDbContext db, CancellationToken ct) =>
        {
            var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (folder is null)
            {
                return Results.NotFound();
            }
            if (!string.IsNullOrWhiteSpace(body.Name))
            {
                folder.Name = body.Name.Trim().Length > 200 ? body.Name.Trim()[..200] : body.Name.Trim();
            }
            // A folder cannot be reparented under itself.
            if (body.ParentId != folder.Id)
            {
                folder.ParentId = body.ParentId;
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.MediaWrite).WithAudit(AuditActions.MediaFolderUpdated, "media_folder");

        // Deleting a folder keeps its assets — they fall back to "unfiled" — and
        // reparents any child folders to the root so nothing is orphaned.
        app.MapDelete("/api/admin/media/folders/{id:guid}", async (
            Guid id, MediaDbContext db, IAuditRecorder audit, AuditScope scope, CancellationToken ct) =>
        {
            var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (folder is null)
            {
                return Results.NotFound();
            }

            // The two reparenting statements are part of this delete, not events of their
            // own — but how much they moved is the interesting part, since an emptied folder
            // and a folder holding a thousand assets look identical afterwards.
            using var _ = scope.SuppressBulkCapture();
            var unfiled = await db.Assets.Where(a => a.FolderId == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.FolderId, (Guid?)null), ct);
            var reparented = await db.Folders.Where(f => f.ParentId == id).ExecuteUpdateAsync(s => s.SetProperty(f => f.ParentId, (Guid?)null), ct);

            audit.Declared?.With("assets_unfiled", unfiled).With("subfolders_reparented", reparented);

            db.Folders.Remove(folder);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.MediaWrite).WithAudit(AuditActions.MediaFolderDeleted, "media_folder");

        // ---- Asset detail / edit / delete ------------------------------------

        app.MapGet("/api/admin/media/{id:guid}", async (Guid id, MediaDbContext db, CancellationToken ct) =>
        {
            var asset = await db.Assets.Include(a => a.Variants).FirstOrDefaultAsync(a => a.Id == id, ct);
            return asset is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    id = asset.Id,
                    category = asset.Category.ToString(),
                    fileName = asset.FileName,
                    contentType = asset.ContentType,
                    status = asset.Status.ToString(),
                    error = asset.Error,
                    sizeBytes = asset.SizeBytes,
                    folderId = asset.FolderId,
                    createdAt = asset.CreatedAt,
                    variants = asset.Variants
                        .OrderBy(v => v.SizeBytes)
                        .Select(v => new { v.Kind, v.Width, v.Height, v.SizeBytes, v.ContentType }),
                });
        }).RequirePermission(PlatformPermissions.MediaRead);

        // Rename an asset (its display file name). Move is a separate bulk endpoint.
        app.MapPatch("/api/admin/media/{id:guid}", async (
            Guid id, RenameAssetRequest body, MediaDbContext db, CancellationToken ct) =>
        {
            var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (asset is null)
            {
                return Results.NotFound();
            }
            var name = body.FileName?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                return Results.BadRequest(new { error = "A file name is required." });
            }
            asset.FileName = Path.GetFileName(name).Length > 512 ? name[..512] : Path.GetFileName(name);
            asset.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.MediaWrite).WithAudit(AuditActions.MediaUpdated, "media_asset");

        // Move one or many assets into a folder (null folderId = back to unfiled).
        app.MapPost("/api/admin/media/move", async (
            MoveAssetsRequest body, MediaDbContext db, IAuditRecorder audit, AuditScope scope, CancellationToken ct) =>
        {
            if (body.Ids is null || body.Ids.Count == 0)
            {
                return Results.BadRequest(new { error = "No assets specified." });
            }
            if (body.FolderId is { } fid && !await db.Folders.AnyAsync(f => f.Id == fid, ct))
            {
                return Results.BadRequest(new { error = "Unknown folder." });
            }
            using var _ = scope.SuppressBulkCapture();
            var moved = await db.Assets
                .Where(a => body.Ids.Contains(a.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.FolderId, body.FolderId), ct);

            // The ids, not just the count: a move is reversible, and only knowing which
            // assets moved makes reversing it possible.
            audit.Declared?
                .With("moved", moved)
                .With("asset_ids", body.Ids)
                .With("folder_id", body.FolderId);

            return Results.Ok(new { moved });
        }).RequirePermission(PlatformPermissions.MediaWrite).WithAudit(AuditActions.MediaMoved, "media_asset");

        // Delete one or many assets: their DB rows (variants cascade) and every
        // stored object (original + renditions). Storage deletes are best-effort.
        app.MapPost("/api/admin/media/delete", async (
            DeleteAssetsRequest body, MediaDbContext db, IObjectStorage storage,
            IOptions<StorageOptions> storageOptions, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (body.Ids is null || body.Ids.Count == 0)
            {
                return Results.BadRequest(new { error = "No assets specified." });
            }
            var logger = loggers.CreateLogger("MediaDelete");
            var bucket = storageOptions.Value.MediaBucket;
            var assets = await db.Assets.Include(a => a.Variants)
                .Where(a => body.Ids.Contains(a.Id))
                .ToListAsync(ct);

            foreach (var asset in assets)
            {
                foreach (var key in asset.Variants.Select(v => v.ObjectKey).Append(asset.OriginalKey))
                {
                    try
                    {
                        await storage.DeleteAsync(bucket, key, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to delete media object {Key}", key);
                    }
                }
            }

            db.Assets.RemoveRange(assets);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { deleted = assets.Count });
        }).RequirePermission(PlatformPermissions.MediaWrite).WithAudit(AuditActions.MediaDeleted, "media_asset");

        // Streams the original (or a named variant, e.g. ?variant=thumb / webp-640)
        // so the admin SPA can render previews behind the bearer token. No public
        // MinIO; bytes flow through admin-api like the content-api delivery path.
        app.MapGet("/api/admin/media/{id:guid}/content", async (
            Guid id, string? variant, HttpContext http, MediaDbContext db, IObjectStorage storage,
            IOptions<StorageOptions> storageOptions, CancellationToken ct) =>
        {
            var asset = await db.Assets.Include(a => a.Variants).FirstOrDefaultAsync(a => a.Id == id, ct);
            if (asset is null)
            {
                return Results.NotFound();
            }

            string key;
            string contentType;
            if (!string.IsNullOrEmpty(variant))
            {
                var v = asset.Variants.FirstOrDefault(x => x.Kind == variant);
                if (v is null)
                {
                    return Results.NotFound();
                }
                key = v.ObjectKey;
                contentType = v.ContentType;
            }
            else
            {
                // The original is only servable once the worker has been over it. Sanitising
                // moved off the request path (see MediaIngestService), so between the upload
                // returning and media-worker finishing, the stored original is exactly the
                // bytes the uploader sent -- EXIF, polyglot payloads and all. Serving those is
                // the one thing that would turn "upload is fast now" into a vulnerability.
                //
                // 409 rather than 404: the asset exists and this will succeed shortly, which is
                // a different thing for a client to do with than "no such asset". Failed says
                // so explicitly, because that one never becomes servable.
                if (asset.Status is MediaStatus.Uploaded or MediaStatus.Processing)
                {
                    return Results.Json(
                        new { error = "This asset is still being processed.", status = asset.Status.ToString() },
                        statusCode: StatusCodes.Status409Conflict);
                }
                if (asset.Status == MediaStatus.Failed)
                {
                    return Results.Json(
                        new { error = asset.Error ?? "This asset could not be processed.", status = "Failed" },
                        statusCode: StatusCodes.Status409Conflict);
                }
                key = asset.OriginalKey;
                contentType = asset.ContentType;
            }

            // Bytes are content-addressed by asset id (+ variant kind), so they're
            // immutable — let the browser cache repeats. Private: it's bearer-gated.
            http.Response.Headers.CacheControl = "private, max-age=86400, immutable";
            var stream = await storage.GetAsync(storageOptions.Value.MediaBucket, key, ct);
            return Results.Stream(stream, contentType, enableRangeProcessing: true);
        }).RequirePermission(PlatformPermissions.MediaRead);

        return app;
    }

    private sealed record CreateFolderRequest(string? Name, Guid? ParentId);
    private sealed record UpdateFolderRequest(string? Name, Guid? ParentId);
    private sealed record RenameAssetRequest(string? FileName);
    private sealed record MoveAssetsRequest(List<Guid>? Ids, Guid? FolderId);
    private sealed record DeleteAssetsRequest(List<Guid>? Ids);
}
