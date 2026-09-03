using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Storage;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;

namespace Dcms.MediaWorker;

/// <summary>
/// Sanitises an uploaded image and generates its webp ladder.
///
/// <para>The sanitising half used to run inside the upload request: a full ImageSharp decode
/// and re-encode of up to 50 MB, on the request thread, before the caller was told anything.
/// It belongs here — this is the process that exists to spend CPU on media, and it is the only
/// one where that CPU is bounded (<see cref="MaxConcurrency"/>), recorded (a failure marks the
/// asset and audits) and measured. An upload now returns as soon as the bytes are stored.</para>
///
/// <para>Ordering is not an implementation detail: the ladder is generated from the
/// <i>sanitised</i> original, never from the bytes as uploaded. Deriving renditions from
/// unsanitised input would re-encode the metadata the sanitiser exists to strip back into every
/// rung, and would decode attacker-controlled bytes twice instead of once.</para>
/// </summary>
public sealed class ImageProcessingConsumer(
    INatsJSContext jetStream,
    IServiceProvider services,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    IEventPublisher events,
    MediaSanitizer sanitizer,
    WebpLadderGenerator webp,
    DcmsMetrics metrics,
    ILogger<ImageProcessingConsumer> logger)
    : MediaConsumerBase(jetStream, services, storage, storageOptions, events, metrics, logger)
{
    protected override string Subject => Subjects.MediaProcessImage;
    protected override string DurableName => "media-worker-image";
    // Four at a time. Image work is a MinIO download, an ImageSharp sanitising re-encode, an
    // ImageSharp re-encode per rung of the webp ladder, and a MinIO upload each -- so it
    // interleaves I/O and CPU well, and one job measured at 4.7s used a single core of four.
    protected override int MaxConcurrency => 4;

    protected override async Task ProcessAsync(MediaProcessRequested job, IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        var asset = await db.Assets.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == job.AssetId, ct)
                    ?? throw new InvalidOperationException($"Media asset {job.AssetId} no longer exists.");

        await using var originalStream = await Storage.GetAsync(Bucket, job.OriginalObjectKey, ct);
        using var memory = new MemoryStream();
        await originalStream.CopyToAsync(memory, ct);
        var original = memory.ToArray();

        // SVG is vector XML with a different threat model and no raster ladder: sanitise it,
        // replace the stored original, and it is done. It reaches this consumer at all only
        // because the sanitising has to happen somewhere off the request path.
        if (asset.ContentType == "image/svg+xml")
        {
            var cleaned = SvgSanitizer.Sanitize(original);
            await ReplaceOriginalAsync(db, asset, cleaned, asset.ContentType, ct);
            await db.SaveChangesAsync(ct);
            await CompleteAsync(scope, job, [], null, ct);
            return;
        }

        // Strips EXIF/IPTC/XMP, neutralises polyglots by re-encoding, and enforces the
        // dimension ceiling. A MediaSanitizationException here fails the asset with its
        // message on the row -- which is what the "Failed" badge in the library shows.
        var sanitized = sanitizer.SanitizeImage(original);
        await ReplaceOriginalAsync(db, asset, sanitized.Data, sanitized.ContentType, ct);

        var variants = webp.Generate(sanitized.Data);
        foreach (var variant in variants)
        {
            var key = StorageKeys.MediaVariant(job.TenantId, job.AssetId, $"{variant.Kind}.webp");
            await UploadBytesAsync(key, variant.Data, variant.ContentType, ct);
            db.Variants.Add(new MediaVariant
            {
                Id = Guid.NewGuid(),
                TenantId = job.TenantId,
                AssetId = job.AssetId,
                Kind = variant.Kind,
                ObjectKey = key,
                ContentType = variant.ContentType,
                SizeBytes = variant.Data.Length,
                Width = variant.Width,
                Height = variant.Height,
            });
        }
        await db.SaveChangesAsync(ct);
        await CompleteAsync(scope, job, variants.Select(v => v.Kind).ToList(), null, ct);
    }

    /// <summary>
    /// Overwrites the stored original with its sanitised form and brings the row back into
    /// agreement with it — size and digest always, and the key when the re-encode settled on a
    /// different container than the sniffer guessed (the extension is part of the key).
    ///
    /// <para>The row is only updated in memory; the caller saves. A key that changed is written
    /// to its new object first and the old one deleted after, so a crash in between leaves a
    /// stray object rather than an asset row pointing at nothing.</para>
    /// </summary>
    private async Task ReplaceOriginalAsync(
        MediaDbContext db, MediaAsset asset, byte[] sanitized, string contentType, CancellationToken ct)
    {
        var extension = MediaFileExtensions.ToExtension(contentType);
        var key = StorageKeys.MediaOriginal(asset.TenantId, asset.Id, extension);

        await UploadBytesAsync(key, sanitized, contentType, ct);

        if (!string.Equals(key, asset.OriginalKey, StringComparison.Ordinal))
        {
            var stale = asset.OriginalKey;
            asset.OriginalKey = key;
            try
            {
                await Storage.DeleteAsync(Bucket, stale, ct);
            }
            catch (Exception ex)
            {
                // Best effort: the row already names the new object, so the old one is dead
                // weight in the bucket, not a correctness problem.
                logger.LogWarning(ex, "Could not remove the superseded original {Key}.", stale);
            }
        }

        asset.ContentType = contentType;
        asset.SizeBytes = sanitized.Length;
        asset.Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(sanitized));
        asset.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
