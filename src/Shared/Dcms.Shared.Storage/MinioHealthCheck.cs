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
        try
        {
            var exists = await client.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(options.Value.MediaBucket),
                cancellationToken);
            return exists
                ? HealthCheckResult.Healthy("MinIO reachable")
                : HealthCheckResult.Degraded($"Bucket '{options.Value.MediaBucket}' missing");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MinIO unreachable", ex);
        }
    }
}
