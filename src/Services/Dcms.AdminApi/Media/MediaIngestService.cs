using System.Security.Cryptography;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Storage;
using Microsoft.Extensions.Options;

namespace Dcms.AdminApi.Media;

/// <summary>
/// Takes raw bytes into tenant media: sniff the real type, sanitize, store the original,
/// record the asset, and dispatch derived renditions.
///
/// <para>Extracted from the upload endpoint when the Meta feed sync needed the same pipeline
/// for images pulled off Meta's CDN. Sharing it is the point rather than a convenience: bytes
/// fetched from a third party are exactly the bytes that most need the EXIF stripping and the
/// re-encode, and a second, simpler "just save it" path would have quietly skipped both.</para>
/// </summary>
public sealed class MediaIngestService(
    MediaSanitizer sanitizer,
    MediaDbContext db,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    IEventPublisher events,
    ITenantContext tenant)
{
    public const long MaxInlineBytes = 50 * 1024 * 1024;

    /// <summary>Why an ingest was refused. The upload endpoint turns these into 400s.</summary>
    public sealed record Result(Guid? AssetId, MediaCategory? Category, string? ContentType, MediaStatus Status, string? Error)
    {
        public bool Ok => Error is null;
        public static Result Failed(string error) => new(null, null, null, MediaStatus.Uploaded, error);
    }

    /// <summary>
    /// Ingests one file. <paramref name="tenantId"/> is explicit because the sync worker runs
    /// on a timer with no request and therefore no ambient tenant.
    /// </summary>
    public async Task<Result> IngestAsync(
        byte[] bytes,
        string fileName,
        Guid? folderId,
        Guid? createdBy,
        CancellationToken ct,
        Guid? tenantId = null)
    {
        if (bytes.Length == 0) return Result.Failed("Empty file.");
        if (bytes.Length > MaxInlineBytes) return Result.Failed("File exceeds the 50 MB inline upload limit.");

        // A wider header than a magic number needs, so a text SVG with an
        // <?xml …?> prolog or a leading comment is still recognisable.
        var sniff = ContentSniffer.Sniff(bytes.AsSpan(0, Math.Min(bytes.Length, 1024)));
        if (sniff is null) return Result.Failed("Unsupported or unrecognized file type.");

        var contentType = sniff.ContentType;
        // Re-encode images to strip metadata / neutralize polyglots. SVG can't
        // be raster-re-encoded, so it takes the XML-sanitizer path (strips
        // scripts, event handlers and dangerous URIs) instead.
        var isSvg = contentType == "image/svg+xml";
        if (sniff.Category == MediaCategory.Image)
        {
            try
            {
                if (isSvg)
                {
                    bytes = SvgSanitizer.Sanitize(bytes);
                }
                else
                {
                    var sanitized = sanitizer.SanitizeImage(bytes);
                    bytes = sanitized.Data;
                    contentType = sanitized.ContentType;
                }
            }
            catch (MediaSanitizationException ex)
            {
                return Result.Failed(ex.Message);
            }
        }

        // SVG is a vector image with no derived renditions: like a File, it is
        // Ready on upload and never dispatched to the raster worker.
        var hasDerivedRenditions = sniff.Category != MediaCategory.File && !isSvg;

        var effectiveTenantId = tenantId ?? tenant.TenantId!.Value;
        var assetId = Guid.NewGuid();
        var ext = MediaExtensions.ToExtension(contentType);
        var key = StorageKeys.MediaOriginal(effectiveTenantId, assetId, ext);

        await using (var upload = new MemoryStream(bytes))
        {
            await storage.PutAsync(storageOptions.Value.MediaBucket, key, upload, bytes.Length, contentType, ct);
        }

        var asset = new MediaAsset
        {
            Id = assetId,
            TenantId = effectiveTenantId,
            Category = sniff.Category,
            FileName = Path.GetFileName(fileName),
            ContentType = contentType,
            SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            OriginalKey = key,
            FolderId = folderId,
            Status = hasDerivedRenditions ? MediaStatus.Uploaded : MediaStatus.Ready,
            CreatedBy = createdBy,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync(ct);

        // Dispatch async processing for media that has derived renditions.
        var subject = MediaExtensions.ProcessSubject(sniff.Category);
        if (hasDerivedRenditions && !string.IsNullOrEmpty(subject))
        {
            asset.Status = MediaStatus.Processing;
            await db.SaveChangesAsync(ct);
            await events.PublishAsync(subject, new MediaProcessRequested(
                Guid.NewGuid(), DateTimeOffset.UtcNow, effectiveTenantId, assetId, sniff.Category, key, contentType), ct);
        }

        return new Result(assetId, sniff.Category, contentType, asset.Status, null);
    }
}
