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
/// Takes an upload into tenant media: sniff the real type, store the bytes exactly as they
/// arrived, record the asset, and hand every derived output to the media queue.
///
/// <para><b>Nothing is decoded, re-encoded or resized here.</b> It used to be: an image was run
/// through ImageSharp on the request thread to strip metadata and neutralise polyglots, which
/// meant a full decode plus a full re-encode of up to 50 MB before the caller got a response —
/// on a four-core host whose cores are also serving every other request. One large upload was
/// seconds of CPU inside the request, and a handful at once starved the rest of the platform.
///
/// <para>That work did not become optional; it moved. <c>ImageProcessingConsumer</c> now
/// sanitises the stored original <i>before</i> it generates the webp ladder — in the worker
/// that already exists for exactly this kind of CPU, with its own concurrency limit, its own
/// failure recording and its own metrics. Until that pass has run the asset is
/// <see cref="MediaStatus.Processing"/> and its original is not servable; see the
/// <c>/content</c> endpoint, which refuses anything that is not <see cref="MediaStatus.Ready"/>
/// rather than hand out bytes that have not been through the sanitiser.</para>
///
/// <para>Shared with the Meta feed sync, which pulls images off Meta's CDN. Sharing is the
/// point rather than a convenience: third-party bytes are exactly the ones that most need the
/// sanitising pass, and a second "just save it" path would quietly skip it.</para>
/// </summary>
public sealed class MediaIngestService(
    MediaDbContext db,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    IEventPublisher events,
    ITenantContext tenant)
{
    public const long MaxInlineBytes = 50 * 1024 * 1024;

    /// <summary>
    /// How much of the file the sniffer sees. Wider than any magic number needs, because SVG is
    /// text: an <c>&lt;?xml …?&gt;</c> prolog or a leading comment has to fit before the root
    /// element for it to be recognised at all.
    /// </summary>
    private const int SniffWindow = 1024;

    /// <summary>Why an ingest was refused. The upload endpoint turns these into 400s.</summary>
    public sealed record Result(Guid? AssetId, MediaCategory? Category, string? ContentType, MediaStatus Status, string? Error)
    {
        public bool Ok => Error is null;
        public static Result Failed(string error) => new(null, null, null, MediaStatus.Uploaded, error);
    }

    /// <summary>
    /// Ingests one file from a <b>seekable</b> stream, which is read twice — once to hash, once
    /// to upload — and never copied into a byte array. ASP.NET has already buffered a form file
    /// (to memory or to disk) by the time a handler runs, so its stream always seeks; a stream
    /// that does not is buffered here rather than rejected.
    ///
    /// <para>Two passes and no copy, rather than one pass over a 50 MB array: SHA-256 runs at
    /// gigabytes a second, so the second read costs milliseconds, while the array cost the same
    /// bytes again in the request's own working set. <paramref name="length"/> is required
    /// because MinIO wants the object size before the first byte.</para>
    ///
    /// <para><paramref name="tenantId"/> is explicit because the Meta sync worker runs on a
    /// timer with no request and therefore no ambient tenant.</para>
    /// </summary>
    public async Task<Result> IngestAsync(
        Stream source,
        long length,
        string fileName,
        Guid? folderId,
        Guid? createdBy,
        CancellationToken ct,
        Guid? tenantId = null)
    {
        if (length <= 0) return Result.Failed("Empty file.");
        if (length > MaxInlineBytes) return Result.Failed("File exceeds the 50 MB inline upload limit.");

        MemoryStream? buffered = null;
        try
        {
            if (!source.CanSeek)
            {
                buffered = new MemoryStream();
                await source.CopyToAsync(buffered, ct);
                source = buffered;
            }

            source.Position = 0;
            var header = new byte[(int)Math.Min(length, SniffWindow)];
            var headerLength = await ReadAtLeastAsync(source, header, ct);
            if (headerLength == 0) return Result.Failed("Empty file.");

            var sniff = ContentSniffer.Sniff(header.AsSpan(0, headerLength));
            if (sniff is null) return Result.Failed("Unsupported or unrecognized file type.");

            source.Position = 0;
            using var hasher = SHA256.Create();
            var digest = await hasher.ComputeHashAsync(source, ct);

            var effectiveTenantId = tenantId ?? tenant.TenantId!.Value;
            var assetId = Guid.NewGuid();
            var key = StorageKeys.MediaOriginal(
                effectiveTenantId, assetId, MediaFileExtensions.ToExtension(sniff.ContentType));

            source.Position = 0;
            await storage.PutAsync(storageOptions.Value.MediaBucket, key, source, length, sniff.ContentType, ct);

            return await RecordAsync(
                assetId, effectiveTenantId, sniff, key, length, Convert.ToHexStringLower(digest),
                fileName, folderId, createdBy, ct);
        }
        finally
        {
            if (buffered is not null) await buffered.DisposeAsync();
        }
    }

    /// <summary>Byte-array overload, for callers that already hold the whole file — the Meta
    /// feed sync, which fetched it over HTTP.</summary>
    public async Task<Result> IngestAsync(
        byte[] bytes,
        string fileName,
        Guid? folderId,
        Guid? createdBy,
        CancellationToken ct,
        Guid? tenantId = null)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return await IngestAsync(stream, bytes.Length, fileName, folderId, createdBy, ct, tenantId);
    }

    private async Task<Result> RecordAsync(
        Guid assetId, Guid tenantId, SniffResult sniff, string key, long sizeBytes, string sha256,
        string fileName, Guid? folderId, Guid? createdBy, CancellationToken ct)
    {
        // Everything with a processing pass to run starts in Processing and goes to the worker.
        // SVG is in that set now: it has no raster ladder, but it does need the XML sanitiser,
        // and that is CPU on somebody's thread either way — so it is the worker's. Only File
        // (pdf/zip, stored verbatim, never decoded) is Ready on arrival.
        var subject = MediaExtensions.ProcessSubject(sniff.Category);
        var needsProcessing = sniff.Category != MediaCategory.File;

        var asset = new MediaAsset
        {
            Id = assetId,
            TenantId = tenantId,
            Category = sniff.Category,
            FileName = Path.GetFileName(fileName),
            ContentType = sniff.ContentType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            OriginalKey = key,
            FolderId = folderId,
            Status = needsProcessing ? MediaStatus.Processing : MediaStatus.Ready,
            CreatedBy = createdBy,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync(ct);

        if (needsProcessing && !string.IsNullOrEmpty(subject))
        {
            await events.PublishAsync(subject, new MediaProcessRequested(
                Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, assetId, sniff.Category, key, sniff.ContentType), ct);
        }

        return new Result(assetId, sniff.Category, sniff.ContentType, asset.Status, null);
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> as far as the stream allows. One ReadAsync is entitled to
    /// return a single byte, and a sniffer handed one byte of a PNG answers "unsupported file
    /// type" — a rejection whose cause nobody could find.
    /// </summary>
    private static async Task<int> ReadAtLeastAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
