using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Media;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// Resolves a media asset id (e.g. an Articles heroImage MediaRef) to its
/// servable variant URLs. Tenant-scoped via the ambient tenant context.
/// </summary>
public sealed class MediaResolver(MediaDbContext db) : IMediaResolver
{
    public async Task<MediaAssetDto?> ResolveAsync(Guid assetId, CancellationToken ct = default)
    {
        var asset = await db.Assets.AsNoTracking()
            .Include(a => a.Variants)
            .FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null)
        {
            return null;
        }

        var urls = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["original"] = $"/api/media/{assetId}/original",
        };
        foreach (var variant in asset.Variants)
        {
            urls[variant.Kind] = $"/api/media/{assetId}/{variant.Kind}";
        }

        return new MediaAssetDto(
            asset.Id,
            (MediaCategory)(int)asset.Category,
            asset.FileName,
            asset.ContentType,
            asset.Status.ToString(),
            urls);
    }
}
