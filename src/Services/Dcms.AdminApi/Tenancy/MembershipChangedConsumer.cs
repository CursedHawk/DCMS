using Dcms.Shared.Caching;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Invalidates the Redis permission cache on membership.changed events from any
/// instance/service (the local mutation path also clears it directly). Resilient
/// to NATS being unavailable at startup — retries without crashing the host.
/// </summary>
public sealed class MembershipChangedConsumer(
    INatsJSContext jetStream,
    ICacheService cache,
    ILogger<MembershipChangedConsumer> logger) : BackgroundService
{
    private const string DurableName = "admin-api-perm-invalidation";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                    Streams.Tenancy,
                    new ConsumerConfig(DurableName)
                    {
                        FilterSubject = Subjects.MembershipChanged,
                        AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<MembershipChanged>(cancellationToken: stoppingToken))
                {
                    try
                    {
                        if (msg.Data is { } evt)
                        {
                            await cache.RemoveAsync(
                                TenancyPermissionResolver.CacheKey(evt.TenantId, evt.UserId), stoppingToken);
                        }
                        await msg.AckAsync(cancellationToken: stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed handling membership.changed; will redeliver.");
                        await msg.NakAsync(cancellationToken: stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Permission-invalidation consumer unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
