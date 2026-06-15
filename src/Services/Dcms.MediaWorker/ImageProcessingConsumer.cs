using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;

namespace Dcms.MediaWorker;

/// <summary>Generates the webp ladder for uploaded images.</summary>
public sealed class ImageProcessingConsumer(
    INatsJSContext jetStream,
    IServiceProvider services,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    IEventPublisher events,
    WebpLadderGenerator webp,
    ILogger<ImageProcessingConsumer> logger)
    : MediaConsumerBase(jetStream, services, storage, storageOptions, events, logger)
{
    protected override string Subject => Subjects.MediaProcessImage;
    protected override string DurableName => "media-worker-image";
    protected override int MaxAckPending => 4;

    protected override async Task ProcessAsync(MediaProcessRequested job, IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();

        await using var originalStream = await Storage.GetAsync(Bucket, job.OriginalObjectKey, ct);
        using var memory = new MemoryStream();
        await originalStream.CopyToAsync(memory, ct);

        var variants = webp.Generate(memory.ToArray());
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
}
