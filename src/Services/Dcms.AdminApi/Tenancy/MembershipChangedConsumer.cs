using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Invalidates the Redis permission cache on membership.changed events from any
/// instance/service (the local mutation path also clears it directly), and mirrors Owner into
/// the tenant's other environments (<see cref="WorkspaceMembers"/>). Resilient
/// to NATS being unavailable at startup — retries without crashing the host.
/// </summary>
public sealed class MembershipChangedConsumer(
    INatsJSContext jetStream,
    IServiceScopeFactory scopes,
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
                            await using var scope = scopes.CreateAsyncScope();
                            await scope.ServiceProvider.GetRequiredService<TenancyPermissionResolver>()
                                .InvalidateAsync(evt.TenantId, evt.UserId, stoppingToken);
                            // Owner in production is Owner in every environment (ADR 0028).
                            await scope.ServiceProvider.GetRequiredService<WorkspaceMembers>()
                                .SyncOwnerAsync(evt.TenantId, evt.UserId, stoppingToken);
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
