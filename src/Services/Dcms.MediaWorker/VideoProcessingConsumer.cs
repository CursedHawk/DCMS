using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Storage;
using Dcms.Shared.Telemetry;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;

namespace Dcms.MediaWorker;

/// <summary>Transcodes uploaded videos to an HLS ladder + poster.</summary>
public sealed class VideoProcessingConsumer(
    INatsJSContext jetStream,
    IServiceProvider services,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    IEventPublisher events,
    VideoTranscoder transcoder,
    DcmsMetrics metrics,
    ILogger<VideoProcessingConsumer> logger)
    : MediaConsumerBase(jetStream, services, storage, storageOptions, events, metrics, logger)
{
    protected override string Subject => Subjects.MediaProcessVideo;
    protected override string DurableName => "media-worker-video";
    protected override int MaxAckPending => 1; // transcoding is CPU-heavy

    protected override async Task ProcessAsync(MediaProcessRequested job, IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        // Guid, not the asset id: the asset id is the one thing two concurrent runs of
        // this job would share. A JetStream redelivery that overlaps the original -- or
        // simply two replicas handed the same asset -- would otherwise transcode into the
        // same directory and the loser would upload the winner's half-written segments.
        var workDir = Path.Combine(Path.GetTempPath(), $"dcms-vid-{job.AssetId:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var input = await DownloadToTempAsync(job.OriginalObjectKey, ".mp4", ct);

        try
        {
            var result = await transcoder.TranscodeAsync(input, workDir, ct);

            // Upload the whole hls/ tree (playlists + segments).
            foreach (var file in result.HlsFiles)
            {
                var key = StorageKeys.MediaVariant(job.TenantId, job.AssetId, $"hls/{file.Name}");
                await UploadFileAsync(key, file.LocalPath, file.ContentType, ct);
            }
            var posterKey = StorageKeys.MediaVariant(job.TenantId, job.AssetId, "poster.jpg");
            await UploadFileAsync(posterKey, result.PosterPath, "image/jpeg", ct);

            // Record the playlists + poster as addressable variants (segments are
            // served via the hls/ path, not individually tracked).
            AddVariant(db, job, "hls-master", $"hls/master.m3u8", "application/vnd.apple.mpegurl");
            foreach (var height in result.Renditions)
            {
                AddVariant(db, job, $"hls-{height}", $"hls/r{height}.m3u8", "application/vnd.apple.mpegurl");
            }
            AddVariant(db, job, "poster", "poster.jpg", "image/jpeg");
            await db.SaveChangesAsync(ct);

            var kinds = new List<string> { "hls-master", "poster" };
            kinds.AddRange(result.Renditions.Select(h => $"hls-{h}"));
            await CompleteAsync(scope, job, kinds, null, ct);
        }
        finally
        {
            TryDelete(input);
            TryDeleteDir(workDir);
        }
    }

    private static void AddVariant(MediaDbContext db, MediaProcessRequested job, string kind, string relative, string contentType)
        => db.Variants.Add(new MediaVariant
        {
            Id = Guid.NewGuid(),
            TenantId = job.TenantId,
            AssetId = job.AssetId,
            Kind = kind,
            ObjectKey = StorageKeys.MediaVariant(job.TenantId, job.AssetId, relative),
            ContentType = contentType,
        });

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
