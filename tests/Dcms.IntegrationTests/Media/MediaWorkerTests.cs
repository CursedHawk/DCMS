extern alias MediaWorkerApp;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using NATS.Client.Core;
using NATS.Client.Serializers.Json;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Testcontainers.Minio;
using Testcontainers.Nats;
using Testcontainers.PostgreSql;

namespace Dcms.IntegrationTests.Media;

/// <summary>
/// Verifies the image pipeline: a published media.process.image job is consumed
/// by media-worker, which loads the original from MinIO, generates the webp
/// ladder, writes media_variants and marks the asset Ready.
/// </summary>
public class MediaWorkerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
    private readonly NatsContainer _nats = new NatsBuilder("nats:2.11").WithCommand("--jetstream").Build();
    private readonly MinioContainer _minio = new MinioBuilder("minio/minio:latest").Build();

    private WebApplicationFactory<MediaWorkerApp::Program> _worker = null!;
    private const string Bucket = "dcms-media";

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _nats.StartAsync(), _minio.StartAsync());

        await using (var nats = new NatsClient(_nats.GetConnectionString()))
        {
            var js = nats.CreateJetStreamContext();
            await js.CreateStreamAsync(new StreamConfig("MEDIA", ["media.process.>"]));
            await js.CreateStreamAsync(new StreamConfig("MEDIA_EVENTS", ["media.processed", "media.failed"]));
            await js.CreateStreamAsync(new StreamConfig("AUDIT", ["audit.>"]));
        }

        await using (var db = NewDb(Guid.Empty))
        {
            await db.Database.MigrateAsync();
        }

        // The audit schema, and it is not optional here even though nothing in this test
        // asserts on audit.
        //
        // Every SaveChangesAsync in a DCMS service goes through the audit interceptor, which
        // writes a row to audit.audit_outbox in the same transaction. Without that table the
        // worker's insert of the media variants fails with 42P01, the consumer's catch block
        // marks the asset failed -- and that write fails for the same reason and is swallowed
        // by MarkFailedAsync's own catch. The result is a message that arrives, is processed,
        // is acked, and leaves the asset in Processing with nothing recorded anywhere: from
        // the outside, a worker that silently does nothing.
        await using (var audit = new AuditDbContext(
            new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options))
        {
            await audit.Database.MigrateAsync();
        }

        var endpoint = $"{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";
        await MinioClientFactory(endpoint, _minio.GetAccessKey(), _minio.GetSecretKey()).MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket));

        _worker = new WebApplicationFactory<MediaWorkerApp::Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            b.UseSetting("Nats:Url", _nats.GetConnectionString());
            b.UseSetting("Storage:Endpoint", endpoint);
            b.UseSetting("Storage:AccessKey", _minio.GetAccessKey());
            b.UseSetting("Storage:SecretKey", _minio.GetSecretKey());
            b.UseSetting("Storage:UseSsl", "false");
            b.UseSetting("Storage:MediaBucket", Bucket);
        });
        using (_worker.CreateClient()) { } // start hosted consumer
    }

    /// <summary>
    /// This was quarantined for a while, and what it took to un-quarantine it is worth
    /// recording: every outward signal said the system was healthy. The MEDIA stream held the
    /// message, the consumer existed with the right filter subject, the stored payload was
    /// well-formed JSON matching the contract, and the delivery counters were spotless --
    /// pending 0, ack-pending 0, redelivered 0. The message really had been delivered and
    /// really had been acked. It just did nothing.
    ///
    /// The cause was this fixture, not the worker: it migrated the media schema and not the
    /// audit schema, so the variant insert hit a missing audit.audit_outbox, and the failure
    /// path that should have recorded that failed identically and swallowed itself.
    ///
    /// Two things came out of it besides the missing migration. MediaConsumerBase no longer
    /// discards an unreadable message in silence, and this fixture now creates every schema
    /// and stream the worker touches rather than only the ones the assertions mention.
    /// </summary>
    [DockerFact]
    public async Task Image_job_produces_webp_variants_and_marks_asset_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var key = StorageKeys.MediaOriginal(tenantId, assetId, ".png");

        // Upload an original image and seed the asset row.
        var endpoint = $"{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";
        var png = CreatePng(1000, 800);
        await using (var ms = new MemoryStream(png))
        {
            await MinioClientFactory(endpoint, _minio.GetAccessKey(), _minio.GetSecretKey()).PutObjectAsync(new PutObjectArgs()
                .WithBucket(Bucket).WithObject(key).WithStreamData(ms).WithObjectSize(png.Length)
                .WithContentType("image/png"), ct);
        }
        await using (var db = NewDb(tenantId))
        {
            db.Assets.Add(new MediaAsset
            {
                Id = assetId, TenantId = tenantId, Category = MediaCategory.Image,
                FileName = "pic.png", ContentType = "image/png", SizeBytes = png.Length,
                OriginalKey = key, Status = MediaStatus.Processing,
            });
            await db.SaveChangesAsync(ct);
        }

        // Dispatch the processing job, with the SAME serializer registry the worker consumes
        // with (AddDcmsMessaging configures NatsJsonSerializerRegistry.Default).
        await using (var conn = new NatsConnection(NatsOpts.Default with
        {
            Url = _nats.GetConnectionString(),
            SerializerRegistry = NatsJsonSerializerRegistry.Default,
        }))
        {
            var js = new NatsJSContext(conn);
            await js.PublishAsync("media.process.image", new MediaProcessRequested(
                Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, assetId, MediaCategory.Image, key, "image/png"),
                cancellationToken: ct);
        }

        // Poll until the worker has produced variants and marked the asset ready.
        await PollUntil(async () =>
        {
            await using var db = NewDb(tenantId);
            var asset = await db.Assets.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == assetId, ct);
            var variants = await db.Variants.IgnoreQueryFilters().Where(v => v.AssetId == assetId).ToListAsync(ct);
            return asset?.Status == MediaStatus.Ready && variants.Count >= 3;
        }, TimeSpan.FromSeconds(30));

        await using var verify = NewDb(tenantId);
        var kinds = await verify.Variants.IgnoreQueryFilters()
            .Where(v => v.AssetId == assetId).Select(v => v.Kind).ToListAsync(ct);
        kinds.Should().Contain(["webp-320", "webp-640", "thumb"]);
    }

    private MediaDbContext NewDb(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<MediaDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options;
        return new MediaDbContext(options, new FixedTenant(tenantId));
    }

    private static IMinioClient MinioClientFactory(string endpoint, string accessKey, string secretKey) => new MinioClient()
        .WithEndpoint(endpoint).WithCredentials(accessKey, secretKey).WithSSL(false).Build();

    private static byte[] CreatePng(int w, int h)
    {
        using var image = new Image<Rgba32>(w, h, new Rgba32(30, 90, 200));
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static async Task PollUntil(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(500);
        }
        throw new TimeoutException("Condition not met within timeout.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_worker is not null) await _worker.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _nats.DisposeAsync().AsTask(), _minio.DisposeAsync().AsTask());
    }

    private sealed class FixedTenant(Guid id) : ITenantContext
    {
        public Guid? TenantId => id == Guid.Empty ? null : id;
        public string? TenantSlug => null;
    }
}
