using System.Text.Json;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Storage;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;

namespace Dcms.MediaWorker;

/// <summary>Normalizes uploaded audio to AAC and produces waveform peaks.</summary>
public sealed class AudioProcessingConsumer(
    INatsJSContext jetStream,
    IServiceProvider services,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    IEventPublisher events,
    AudioTranscoder transcoder,
    ILogger<AudioProcessingConsumer> logger)
    : MediaConsumerBase(jetStream, services, storage, storageOptions, events, logger)
{
    protected override string Subject => Subjects.MediaProcessAudio;
    protected override string DurableName => "media-worker-audio";

    protected override async Task ProcessAsync(MediaProcessRequested job, IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        var workDir = Path.Combine(Path.GetTempPath(), $"dcms-aud-{job.AssetId:N}");
        Directory.CreateDirectory(workDir);
        var input = await DownloadToTempAsync(job.OriginalObjectKey, Path.GetExtension(job.OriginalObjectKey), ct);

        try
        {
            var result = await transcoder.TranscodeAsync(input, workDir, ct);

            var aacKey = StorageKeys.MediaVariant(job.TenantId, job.AssetId, "audio.m4a");
            await UploadFileAsync(aacKey, result.AacPath, "audio/mp4", ct);
            var peaksKey = StorageKeys.MediaVariant(job.TenantId, job.AssetId, "peaks.json");
            await UploadBytesAsync(peaksKey, result.PeaksJson, "application/json", ct);

            db.Variants.Add(NewVariant(job, "aac", aacKey, "audio/mp4"));
            db.Variants.Add(NewVariant(job, "peaks", peaksKey, "application/json"));
            await db.SaveChangesAsync(ct);

            var metadata = JsonSerializer.Serialize(new { durationSeconds = result.Duration.TotalSeconds });
            await CompleteAsync(scope, job, ["aac", "peaks"], metadata, ct);
        }
        finally
        {
            TryDelete(input);
            TryDeleteDir(workDir);
        }
    }

    private static MediaVariant NewVariant(MediaProcessRequested job, string kind, string key, string contentType)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = job.TenantId,
            AssetId = job.AssetId,
            Kind = kind,
            ObjectKey = key,
            ContentType = contentType,
        };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
