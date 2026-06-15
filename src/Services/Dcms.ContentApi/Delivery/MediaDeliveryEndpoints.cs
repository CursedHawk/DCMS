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
            if (variant == "original")
            {
                key = asset.OriginalKey;
                contentType = asset.ContentType;
            }
            else
            {
                var v = await db.Variants.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.AssetId == assetId && x.Kind == variant, ct);
                if (v is null)
                {
                    return Results.NotFound();
                }
                key = v.ObjectKey;
                contentType = v.ContentType;
            }

            Stream stream;
            try
            {
                stream = await storage.GetAsync(storageOptions.Value.MediaBucket, key, ct);
            }
            catch (Minio.Exceptions.ObjectNotFoundException)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.Stream(stream, contentType, enableRangeProcessing: true);
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
            Stream stream;
            try
            {
                stream = await storage.GetAsync(storageOptions.Value.MediaBucket, key, ct);
            }
            catch (Minio.Exceptions.ObjectNotFoundException)
            {
                return Results.NotFound();
            }

            var contentType = HlsContentType(file);
            // Playlists may change between publishes; segments are immutable.
            http.Response.Headers.CacheControl = file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? "public, max-age=60"
                : "public, max-age=31536000, immutable";
            return Results.Stream(stream, contentType, enableRangeProcessing: true);
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
