using Dcms.Shared.Data.Media;
using Dcms.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// Serves media bytes by proxying from MinIO: /api/media/{assetId}/{variant}
/// where variant is "original" or a variant kind (e.g. webp-640). Variant keys
/// are content-addressed by asset id, so responses are immutably cacheable.
/// Tenant-scoped via the ambient tenant context.
/// </summary>
public static class MediaDeliveryEndpoints
{
    public static IEndpointRouteBuilder MapMediaDelivery(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/media/{assetId:guid}/{variant}", async (
            Guid assetId, string variant, HttpContext http, MediaDbContext db,
            IObjectStorage storage, IOptions<StorageOptions> storageOptions, CancellationToken ct) =>
        {
            var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct);
            if (asset is null)
            {
                return Results.NotFound();
            }

            string key;
            string contentType;
            var exact = true;
            if (variant == "original" && asset.OriginalDeletedAt is not null)
            {
                // The tenant deleted the original to free space (MediaOriginals). Pages still ask
                // for it by this URL, so the copy that stands in for it answers.
                var variants = await db.Variants.AsNoTracking().Where(x => x.AssetId == assetId).ToListAsync(ct);
                var standIn = MediaOriginals.StandIn(asset.Category, variants);
                if (standIn is null)
                {
                    return Results.NotFound();
                }
                key = standIn.ObjectKey;
                contentType = standIn.ContentType;
            }
            else if (variant == "original")
            {
                key = asset.OriginalKey;
                contentType = asset.ContentType;
            }
            else
            {
                var v = await db.Variants.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.AssetId == assetId && x.Kind == variant, ct);
                if (v is null && WebpLadder.WidthOf(variant) is not null)
                {
                    // A WebP width this asset was not made at: sites ask for the whole ladder in a
                    // srcset, and a picture narrower than a width has no variant of it. Serve the
                    // nearest instead, so every candidate loads.
                    var ladder = await db.Variants.AsNoTracking()
                        .Where(x => x.AssetId == assetId && x.Kind.StartsWith("webp-"))
                        .ToListAsync(ct);
                    v = ladder.FirstOrDefault(x => x.Kind == WebpLadder.Nearest(variant, ladder.Select(l => l.Kind)));
                    exact = false;
                    if (v is null && asset.ContentType.StartsWith("image/", StringComparison.Ordinal))
                    {
                        // No ladder at all — still being made, or a format the encoder cannot read
                        // (SVG): the original is the picture.
                        http.Response.Headers.CacheControl = "public, max-age=300";
                        return await ObjectStreaming.WriteObjectAsync(
                            http, storage, storageOptions.Value.MediaBucket, asset.OriginalKey, asset.ContentType, ct);
                    }
                }
                if (v is null)
                {
                    return Results.NotFound();
                }
                key = v.ObjectKey;
                contentType = v.ContentType;
            }

            // A stand-in may be replaced by the real thing once the media worker catches up.
            http.Response.Headers.CacheControl = exact ? "public, max-age=31536000, immutable" : "public, max-age=3600";
            return await ObjectStreaming.WriteObjectAsync(
                http, storage, storageOptions.Value.MediaBucket, key, contentType, ct);
        });

        // HLS playlists + segments live under the asset's hls/ prefix. Segments are
        // not individually tracked in the DB, so the key is composed from the
        // (tenant-verified) asset and the requested file name.
        app.MapGet("/api/media/{assetId:guid}/hls/{**file}", async (
            Guid assetId, string file, HttpContext http, MediaDbContext db,
            IObjectStorage storage, IOptions<StorageOptions> storageOptions, CancellationToken ct) =>
        {
            if (file.Contains("..", StringComparison.Ordinal))
            {
                return Results.BadRequest();
            }
            var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct);
            if (asset is null)
            {
                return Results.NotFound();
            }

            var key = StorageKeys.MediaVariant(asset.TenantId, assetId, $"hls/{file}");
            var contentType = HlsContentType(file);
            // Playlists may change between publishes; segments are immutable.
            http.Response.Headers.CacheControl = file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? "public, max-age=60"
                : "public, max-age=31536000, immutable";
            return await ObjectStreaming.WriteObjectAsync(
                http, storage, storageOptions.Value.MediaBucket, key, contentType, ct);
        });

        return app;
    }

    private static string HlsContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".m3u8" => "application/vnd.apple.mpegurl",
        ".ts" => "video/mp2t",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream",
    };
}
