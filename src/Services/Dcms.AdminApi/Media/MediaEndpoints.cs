using System.Security.Cryptography;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Storage;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Media;

public static class MediaEndpoints
{
    private const long MaxInlineBytes = 50 * 1024 * 1024;

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/media", async (
            IFormFile file, MediaSanitizer sanitizer, MediaDbContext db, IObjectStorage storage,
            IOptions<StorageOptions> storageOptions, IEventPublisher events, ITenantContext tenant,
            CurrentUser me, CancellationToken ct) =>
        {
            if (file.Length == 0)
            {
                return Results.BadRequest(new { error = "Empty file." });
            }
            if (file.Length > MaxInlineBytes)
            {
                return Results.BadRequest(new { error = "File exceeds the 50 MB inline upload limit." });
            }

            await using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();

            var sniff = ContentSniffer.Sniff(bytes.AsSpan(0, Math.Min(bytes.Length, 32)));
            if (sniff is null)
            {
                return Results.BadRequest(new { error = "Unsupported or unrecognized file type." });
            }

            var contentType = sniff.ContentType;
            // Re-encode images to strip metadata / neutralize polyglots.
            if (sniff.Category == MediaCategory.Image)
            {
                try
                {
                    var sanitized = sanitizer.SanitizeImage(bytes);
                    bytes = sanitized.Data;
                    contentType = sanitized.ContentType;
                }
                catch (MediaSanitizationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }

            var tenantId = tenant.TenantId!.Value;
            var assetId = Guid.NewGuid();
            var ext = MediaExtensions.ToExtension(contentType);
            var key = StorageKeys.MediaOriginal(tenantId, assetId, ext);

            await using (var upload = new MemoryStream(bytes))
            {
                await storage.PutAsync(storageOptions.Value.MediaBucket, key, upload, bytes.Length, contentType, ct);
            }

            var asset = new MediaAsset
            {
                Id = assetId,
                TenantId = tenantId,
                Category = sniff.Category,
                FileName = Path.GetFileName(file.FileName),
                ContentType = contentType,
                SizeBytes = bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                OriginalKey = key,
                Status = sniff.Category == MediaCategory.File ? MediaStatus.Ready : MediaStatus.Uploaded,
                CreatedBy = me.UserId,
            };
            db.Assets.Add(asset);
            await db.SaveChangesAsync(ct);

            // Dispatch async processing for media that has derived renditions.
            var subject = MediaExtensions.ProcessSubject(sniff.Category);
            if (!string.IsNullOrEmpty(subject))
            {
                asset.Status = MediaStatus.Processing;
                await db.SaveChangesAsync(ct);
                await events.PublishAsync(subject, new MediaProcessRequested(
                    Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, assetId, sniff.Category, key, contentType), ct);
            }

            return Results.Created($"/api/admin/media/{assetId}", new
            {
                id = assetId,
                category = sniff.Category.ToString(),
                contentType,
                status = asset.Status.ToString(),
            });
        }).RequirePermission(PlatformPermissions.MediaWrite).DisableAntiforgery();

        app.MapGet("/api/admin/media", async (MediaDbContext db, CancellationToken ct) =>
        {
            var assets = await db.Assets
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => new
                {
                    id = a.Id,
                    category = a.Category.ToString(),
                    fileName = a.FileName,
                    status = a.Status.ToString(),
                    sizeBytes = a.SizeBytes,
                })
                .ToListAsync(ct);
            return Results.Ok(assets);
        }).RequirePermission(PlatformPermissions.MediaRead);

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
                    variants = asset.Variants.Select(v => new { v.Kind, v.Width, v.Height, v.SizeBytes }),
                });
        }).RequirePermission(PlatformPermissions.MediaRead);

        // Streams the original (or a named variant, e.g. ?variant=thumb / webp-640)
        // so the admin SPA can render previews behind the bearer token. No public
        // MinIO; bytes flow through admin-api like the content-api delivery path.
        app.MapGet("/api/admin/media/{id:guid}/content", async (
            Guid id, string? variant, MediaDbContext db, IObjectStorage storage,
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
                key = asset.OriginalKey;
                contentType = asset.ContentType;
            }

            var stream = await storage.GetAsync(storageOptions.Value.MediaBucket, key, ct);
            return Results.Stream(stream, contentType, enableRangeProcessing: true);
        }).RequirePermission(PlatformPermissions.MediaRead);

        return app;
    }
}
