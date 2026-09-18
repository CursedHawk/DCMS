using System.Net;
using System.Net.Http.Headers;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace Dcms.IntegrationTests.Media;

/// <summary>
/// Verifies content-api serves HLS playlists and segments from MinIO with the
/// right content types and Range support — without needing ffmpeg (fake HLS
/// artifacts are uploaded directly).
/// </summary>
public class HlsServingTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Build();
    private readonly MinioContainer _minio = TestMinio.Build();

    private WebApplicationFactory<Program> _content = null!;
    private const string Bucket = "dcms-media";
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _assetId = Guid.NewGuid();
    private const string Slug = "hls-tenant";

    /// <summary>A segment whose every slice is distinct (251 is prime, so no 256-byte repeat),
    /// so a range served from the wrong offset cannot pass by coincidence the way zeros would.</summary>
    private static readonly byte[] Segment = [.. Enumerable.Range(0, 4096).Select(i => (byte)(i % 251))];

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _minio.StartAsync());
        var endpoint = $"{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";
        var minio = new MinioClient().WithEndpoint(endpoint)
            .WithCredentials(_minio.GetAccessKey(), _minio.GetSecretKey()).WithSSL(false).Build();
        await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket));

        // Migrate + seed a tenant and a ready video asset.
        var conn = _postgres.GetConnectionString();
        await using (var tenancy = new TenancyDbContext(
            new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(conn).Options, new NullCtx()))
        {
            await tenancy.Database.MigrateAsync();
            tenancy.Tenants.Add(new Tenant { Id = _tenantId.ToString(), Identifier = Slug, Name = "HLS" });
            await tenancy.SaveChangesAsync();
        }
        await using (var media = new MediaDbContext(
            new DbContextOptionsBuilder<MediaDbContext>().UseNpgsql(conn).Options, new FixedCtx(_tenantId)))
        {
            await media.Database.MigrateAsync();
            media.Assets.Add(new MediaAsset
            {
                Id = _assetId, TenantId = _tenantId, Category = MediaCategory.Video,
                FileName = "v.mp4", ContentType = "video/mp4", OriginalKey = "x", Status = MediaStatus.Ready,
            });
            await media.SaveChangesAsync();
        }

        // Upload fake HLS artifacts under the asset's hls/ prefix.
        await PutText(minio, "hls/master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2800000\nr720.m3u8\n");
        await PutText(minio, "hls/r720.m3u8", "#EXTM3U\n#EXTINF:6.0,\nr720_000.ts\n#EXT-X-ENDLIST\n");
        await PutBytes(minio, "hls/r720_000.ts", Segment);

        _content = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", conn);
            b.UseSetting("ConnectionStrings:Redis", "localhost:1");
            b.UseSetting("Nats:Url", "nats://localhost:1");
            b.UseSetting("Storage:Endpoint", endpoint);
            b.UseSetting("Storage:AccessKey", _minio.GetAccessKey());
            b.UseSetting("Storage:SecretKey", _minio.GetSecretKey());
            b.UseSetting("Storage:UseSsl", "false");
            b.UseSetting("Storage:MediaBucket", Bucket);
        });
        using (_content.CreateClient()) { }
    }

    [DockerFact]
    public async Task Serves_playlist_and_segment_with_correct_types_and_range()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = _content.CreateClient();

        var master = Req($"/api/media/{_assetId}/hls/master.m3u8");
        var masterRes = await client.SendAsync(master, ct);
        masterRes.StatusCode.Should().Be(HttpStatusCode.OK);
        masterRes.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.apple.mpegurl");

        // Range request on a segment → 206 Partial Content.
        var seg = Req($"/api/media/{_assetId}/hls/r720_000.ts");
        seg.Headers.Range = new RangeHeaderValue(0, 1023);
        var segRes = await client.SendAsync(seg, ct);
        segRes.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        segRes.Content.Headers.ContentType!.MediaType.Should().Be("video/mp2t");
        segRes.Content.Headers.ContentRange!.ToString().Should().Be("bytes 0-1023/4096");
        (await segRes.Content.ReadAsByteArrayAsync(ct)).Should().Equal(Segment[..1024]);

        // PERF-01: ranges are now pulled from MinIO by offset rather than sliced out of a fully
        // downloaded buffer, so a NON-zero offset is the case that proves it. A suffix range is
        // what a player sends for an MP4's trailing moov atom.
        var tail = Req($"/api/media/{_assetId}/hls/r720_000.ts");
        tail.Headers.Range = new RangeHeaderValue(null, 100);
        var tailRes = await client.SendAsync(tail, ct);
        tailRes.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        tailRes.Content.Headers.ContentRange!.ToString().Should().Be("bytes 3996-4095/4096");
        (await tailRes.Content.ReadAsByteArrayAsync(ct)).Should().Equal(Segment[3996..]);

        // No Range header → the whole object, streamed, with its length declared.
        var whole = await client.SendAsync(Req($"/api/media/{_assetId}/hls/r720_000.ts"), ct);
        whole.StatusCode.Should().Be(HttpStatusCode.OK);
        whole.Content.Headers.ContentLength.Should().Be(4096);
        (await whole.Content.ReadAsByteArrayAsync(ct)).Should().Equal(Segment);

        // A range past the end is refused, and says how big the object is.
        var past = Req($"/api/media/{_assetId}/hls/r720_000.ts");
        past.Headers.Range = new RangeHeaderValue(5000, null);
        var pastRes = await client.SendAsync(past, ct);
        pastRes.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        pastRes.Content.Headers.ContentRange!.ToString().Should().Be("bytes */4096");

        // A missing segment is still a 404, not a 500 from the stat.
        (await client.SendAsync(Req($"/api/media/{_assetId}/hls/r720_999.ts"), ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Cross-tenant isolation: a different tenant slug cannot see the asset.
        var foreignReq = new HttpRequestMessage(HttpMethod.Get, $"/api/media/{_assetId}/hls/master.m3u8");
        foreignReq.Headers.Add("X-Dcms-Tenant", "someone-else");
        (await client.SendAsync(foreignReq, ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Storage_streams_exact_byte_ranges_from_minio()
    {
        // The primitive delivery is built on, against a real MinIO: a ranged GET must return
        // exactly the asked-for slice, including from a non-zero offset, with nothing buffered.
        var ct = TestContext.Current.CancellationToken;
        var endpoint = $"{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";
        var storage = new MinioObjectStorage(new MinioClient().WithEndpoint(endpoint)
            .WithCredentials(_minio.GetAccessKey(), _minio.GetSecretKey()).WithSSL(false).Build());
        var key = StorageKeys.MediaVariant(_tenantId, _assetId, "hls/r720_000.ts");

        (await storage.StatAsync(Bucket, key, ct))!.Size.Should().Be(4096);
        (await storage.StatAsync(Bucket, key + ".missing", ct)).Should().BeNull();

        using var head = new MemoryStream();
        await storage.GetToAsync(Bucket, key, head, 0, 1024, ct);
        head.ToArray().Should().Equal(Segment[..1024]);

        using var tail = new MemoryStream();
        await storage.GetToAsync(Bucket, key, tail, 3996, 100, ct);
        tail.ToArray().Should().Equal(Segment[3996..]);

        using var all = new MemoryStream();
        await storage.GetToAsync(Bucket, key, all, ct: ct);
        all.ToArray().Should().Equal(Segment);
    }

    private static HttpRequestMessage Req(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Dcms-Tenant", Slug);
        return req;
    }

    private Task PutText(IMinioClient minio, string name, string content)
        => PutBytes(minio, name, System.Text.Encoding.UTF8.GetBytes(content));

    private async Task PutBytes(IMinioClient minio, string name, byte[] data)
    {
        var key = StorageKeys.MediaVariant(_tenantId, _assetId, name);
        using var ms = new MemoryStream(data);
        await minio.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(key)
            .WithStreamData(ms).WithObjectSize(data.Length).WithContentType("application/octet-stream"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_content is not null) await _content.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _minio.DisposeAsync().AsTask());
    }

    private sealed class NullCtx : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }

    private sealed class FixedCtx(Guid id) : ITenantContext
    {
        public Guid? TenantId => id;
        public string? TenantSlug => null;
    }
}
