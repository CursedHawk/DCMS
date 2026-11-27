using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// Relays <c>notify.changed</c> (a plugin's <c>dcms.notifications@1</c> SignalChange, sent from
/// any service) to the tenant's open consoles as <c>ResourceChanged</c>. Ordered and ephemeral
/// like the platform console's live updates: a hint nobody was there for is not worth keeping,
/// and the console refetches on reconnect anyway.
/// </summary>
public sealed class ConsoleChangeRelay(
    INatsJSContext jetStream,
    ResourceChangePublisher changes,
    ILogger<ConsoleChangeRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrderedConsumerAsync(
                    Streams.Notify,
                    new NatsJSOrderedConsumerOpts
                    {
                        DeliverPolicy = ConsumerConfigDeliverPolicy.New,
                        FilterSubjects = [Subjects.NotifyChanged],
                    },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<ConsoleChangeRequested>(cancellationToken: stoppingToken))
                {
                    if (msg.Data is { } e)
                    {
                        await changes.PublishAsync(e.TenantId, e.Tag, e.Id, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Console change hints unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
