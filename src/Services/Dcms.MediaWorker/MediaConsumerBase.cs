using Dcms.Shared.Audit;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Storage;
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
            await msg.AckAsync(cancellationToken: ct);
            return;
        }
        using var scope = services.CreateScope();

        // The person who uploaded the asset, carried from the upload request. Without it the
        // derivative work reads as the platform acting on its own.
        using var context = msg.RestoreAuditContext(scope.ServiceProvider, job.TenantId);
        var audit = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();

        try
        {
            await ProcessAsync(job, scope, ct);
            await msg.AckAsync(cancellationToken: ct);
        }
        catch (Exception ex)
        {
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
