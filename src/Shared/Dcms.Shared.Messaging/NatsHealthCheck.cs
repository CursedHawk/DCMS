using Microsoft.Extensions.Diagnostics.HealthChecks;
using NATS.Client.Core;

namespace Dcms.Shared.Messaging;

public sealed class NatsHealthCheck(INatsConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rtt = await connection.PingAsync(cancellationToken);
            return HealthCheckResult.Healthy($"NATS ping {rtt.TotalMilliseconds:F0} ms");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("NATS unreachable", ex);
        }
    }
}
