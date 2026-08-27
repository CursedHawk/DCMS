using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace Dcms.Shared.Storage;

public sealed class MinioHealthCheck(IMinioClient client, IOptions<StorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // A bucket this service's credentials are actually allowed to see; see
        // StorageOptions.HealthBucket for why that is not always the media bucket.
        var bucket = string.IsNullOrWhiteSpace(options.Value.HealthBucket)
            ? options.Value.MediaBucket
            : options.Value.HealthBucket;

        try
        {
            var exists = await client.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(bucket),
                cancellationToken);
            return exists
                ? HealthCheckResult.Healthy("MinIO reachable")
                : HealthCheckResult.Degraded($"Bucket '{bucket}' missing");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MinIO unreachable", ex);
        }
    }
}
