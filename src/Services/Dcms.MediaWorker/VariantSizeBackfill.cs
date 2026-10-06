using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.MediaWorker;

/// <summary>
/// Fills in the size of audio and video variants written before 2026-10-06, when those
/// consumers recorded none: the library showed every one as 0 B, and the storage cap counted
/// none of their bytes. Runs once per start and touches only rows still at 0, so after the
/// first pass it is a single empty query.
/// </summary>
// ponytail: one-off backfill; delete once every environment has run it (no non-image variant at 0 B).
public sealed class VariantSizeBackfill(
    IServiceProvider services,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    ILogger<VariantSizeBackfill> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // rls: platform. A maintenance pass over every tenant's variants, by design.
            using var rls = RlsScope.Platform();
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
            var bucket = storageOptions.Value.MediaBucket;

            // Images always recorded their sizes; a WebP or thumb at 0 B is not this bug.
            var rows = await db.Variants.IgnoreQueryFilters()
                .Where(v => v.SizeBytes == 0 && !v.Kind.StartsWith("webp-") && v.Kind != "thumb")
                .ToListAsync(ct);
            foreach (var v in rows)
            {
                v.SizeBytes = IsRendition(v.Kind)
                    // r720.m3u8 → every object starting "r720": its playlist and its segments.
                    ? await storage.SizeOfPrefixAsync(bucket, v.ObjectKey[..^".m3u8".Length], ct)
                    : (await storage.StatAsync(bucket, v.ObjectKey, ct))?.Size ?? 0;
            }
            await db.SaveChangesAsync(ct);
            if (rows.Count > 0)
            {
                logger.LogInformation("Recorded the sizes of {Count} media variants that had none.", rows.Count);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Media variant size backfill failed; it runs again on the next start.");
        }
    }

    private static bool IsRendition(string kind) =>
        kind.StartsWith("hls-", StringComparison.Ordinal) && kind != "hls-master";
}
