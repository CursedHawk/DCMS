using System.Diagnostics;
using Dcms.Shared.Audit;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Storage;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.MediaWorker;

/// <summary>
/// Shared JetStream work-queue loop for media processing: resilient connect,
/// explicit ack, failure recording, and MinIO download/upload helpers. Each
/// concrete consumer binds one subject and implements <see cref="ProcessAsync"/>.
/// </summary>
public abstract class MediaConsumerBase(
    INatsJSContext jetStream,
    IServiceProvider services,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    IEventPublisher events,
    DcmsMetrics metrics,
    ILogger logger) : BackgroundService
{
    protected abstract string Subject { get; }
    protected abstract string DurableName { get; }
    protected virtual int MaxAckPending => 2;

    protected IServiceProvider Services => services;
    protected IObjectStorage Storage => storage;
    protected string Bucket => storageOptions.Value.MediaBucket;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                    Streams.Media,
                    new ConsumerConfig(DurableName)
                    {
                        FilterSubject = Subject,
                        AckPolicy = ConsumerConfigAckPolicy.Explicit,
                        MaxAckPending = MaxAckPending,
                    },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<MediaProcessRequested>(cancellationToken: stoppingToken))
                {
                    await HandleAsync(msg, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Consumer} unavailable; retrying in 5s.", DurableName);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleAsync(INatsJSMsg<MediaProcessRequested> msg, CancellationToken ct)
    {
        var job = msg.Data;
        if (job is null)
        {
            // A message this consumer cannot read. Previously this acked and returned in
            // silence, which is the worst available behaviour: the job disappears, the asset
            // stays in Processing forever, and nothing anywhere records that it happened.
            // The upload looks successful to the tenant and the media never appears.
            //
            // It also made the failure undiagnosable. Every outward signal was healthy -- the
            // stream held the message, the consumer existed with the right filter, delivery
            // and ack counters were clean -- because the message really had been delivered
            // and really had been acked. Three runs of a test went into establishing that
            // nothing had gone wrong, which is exactly what a silent branch buys you.
            //
            // So: say what happened, mark the asset, and only then ack. Still ack, because
            // the message is genuinely unreadable and redelivering it forever would block the
            // work queue behind a poison message.
            var reason = msg.Error?.Message ?? "message payload could not be deserialized";
            logger.LogError(
                "{Consumer} received a message on {Subject} it could not deserialize ({Reason}); " +
                "acking to avoid a poison message. Sequence {Sequence}.",
                DurableName, msg.Subject, reason, msg.Metadata?.Sequence.Stream);
            await msg.AckAsync(cancellationToken: ct);
            return;
        }
        using var scope = services.CreateScope();

        // The person who uploaded the asset, carried from the upload request. Without it the
        // derivative work reads as the platform acting on its own.
        using var context = msg.RestoreAuditContext(scope.ServiceProvider, job.TenantId);
        var audit = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();

        // A span for the job itself. RestoreAuditContext above has already made this a child of
        // the span that requested the upload, so a slow transcode appears inside the trace of
        // the request that caused it rather than as an unexplained gap between two services.
        using var activity = DcmsActivitySource.Start($"media.process.{job.Category}");
        activity?.SetTag("dcms.media.asset_id", job.AssetId.ToString());
        activity?.SetTag("dcms.media.category", job.Category.ToString());

        var started = Stopwatch.GetTimestamp();
        var succeeded = false;

        try
        {
            await ProcessAsync(job, scope, ct);
            await msg.AckAsync(cancellationToken: ct);
            succeeded = true;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            // And the consumer span too: a child's status does not propagate to its parent, so
            // without this a failed transcode counts as a successfully handled message.
            context.Failed(ex.Message);

            logger.LogError(ex, "Failed processing asset {AssetId}", job.AssetId);
            await MarkFailedAsync(job, ex.Message, ct);

            // The upload succeeded and the asset is unusable — a state the tenant will notice
            // and ask about, and one nothing else records.
            audit.Record(AuditActions.MediaProcessingFailed)
                .For("media_asset", job.AssetId)
                .As(AuditCategory.System)
                .Failed(ex.Message);

            await msg.AckAsync(cancellationToken: ct); // recorded; don't redeliver poison messages
        }

        metrics.MediaProcessed(
            job.TenantId,
            // The enum's name, not its number: a dashboard legend reading "1" is useless, and
            // a numeric value would silently change meaning if the enum is ever reordered.
            job.Category.ToString(),
            succeeded,
            succeeded ? await VariantBytesAsync(scope, job.AssetId, ct) : 0,
            Stopwatch.GetElapsedTime(started));

        await audit.FlushAsync(ct);
    }

    protected abstract Task ProcessAsync(MediaProcessRequested job, IServiceScope scope, CancellationToken ct);

    protected async Task<string> DownloadToTempAsync(string objectKey, string extension, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dcms-{Guid.NewGuid():N}{extension}");
        await using var source = await storage.GetAsync(Bucket, objectKey, ct);
        await using var file = File.Create(path);
        await source.CopyToAsync(file, ct);
        return path;
    }

    protected async Task UploadFileAsync(string objectKey, string localPath, string contentType, CancellationToken ct)
    {
        await using var stream = File.OpenRead(localPath);
        await storage.PutAsync(Bucket, objectKey, stream, stream.Length, contentType, ct);
    }

    protected async Task UploadBytesAsync(string objectKey, byte[] data, string contentType, CancellationToken ct)
    {
        await using var stream = new MemoryStream(data);
        await storage.PutAsync(Bucket, objectKey, stream, data.Length, contentType, ct);
    }

    protected async Task CompleteAsync(IServiceScope scope, MediaProcessRequested job, IReadOnlyList<string> variantKinds, string? metadataJson, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        var asset = await db.Assets.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == job.AssetId, ct);
        if (asset is not null)
        {
            asset.Status = MediaStatus.Ready;
            asset.UpdatedAt = DateTimeOffset.UtcNow;
            if (metadataJson is not null)
            {
                asset.MetadataJson = metadataJson;
            }
            await db.SaveChangesAsync(ct);
        }
        await events.PublishAsync(Subjects.MediaProcessed, new MediaProcessed(
            Guid.NewGuid(), DateTimeOffset.UtcNow, job.TenantId, job.AssetId, job.Category, variantKinds), ct);
    }

    /// <summary>
    /// Total bytes this job actually wrote, counted from the variant rows it produced.
    ///
    /// <para>Read back rather than accumulated by the transcoders, because the ladders differ:
    /// images produce a webp set, video produces an HLS tree of segments plus playlists plus a
    /// poster, and audio produces one file plus a waveform. Summing the rows is the only place
    /// where "what did this cost the object store" has one answer for all three.</para>
    ///
    /// <para>Failure here is swallowed. A byte count is a dashboard number, and losing one must
    /// never turn a completed job into a failed one.</para>
    /// </summary>
    private async Task<long> VariantBytesAsync(IServiceScope scope, Guid assetId, CancellationToken ct)
    {
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
            return await db.Variants
                .IgnoreQueryFilters()
                .Where(v => v.AssetId == assetId)
                .SumAsync(v => (long?)v.SizeBytes, ct) ?? 0;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not total variant bytes for {AssetId}.", assetId);
            return 0;
        }
    }

    private async Task MarkFailedAsync(MediaProcessRequested job, string error, CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
            var asset = await db.Assets.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == job.AssetId, ct);
            if (asset is not null)
            {
                asset.Status = MediaStatus.Failed;
                asset.Error = error;
                await db.SaveChangesAsync(ct);
            }
            await events.PublishAsync(Subjects.MediaFailed,
                new MediaFailed(Guid.NewGuid(), DateTimeOffset.UtcNow, job.TenantId, job.AssetId, error), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed recording media failure for {AssetId}", job.AssetId);
        }
    }
}
