using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.AdminApi.Chat;

/// <summary>
/// Durable consumer of the CHAT stream. Realtime delivery to connected agents is
/// already handled by content-api's SignalR Redis backplane; this consumer is the
/// out-of-band fan-out point for offline-agent notifications and unread tracking.
/// For the MVP it records activity; the notification transport is a documented
/// extension. Resilient to NATS being unavailable.
/// </summary>
public sealed class ChatFanoutConsumer(
    INatsJSContext jetStream,
    ILogger<ChatFanoutConsumer> logger) : BackgroundService
{
    private const string DurableName = "admin-api-chat-fanout";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                    Streams.Chat,
                    new ConsumerConfig(DurableName) { AckPolicy = ConsumerConfigAckPolicy.Explicit },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<ChatMessagePosted>(cancellationToken: stoppingToken))
                {
                    try
                    {
                        if (msg.Data is { } e)
                        {
                            logger.LogDebug(
                                "Chat fan-out: tenant {Tenant} conversation {Conversation} message {Message} from {Sender}.",
                                e.TenantId, e.ConversationId, e.MessageId, e.Sender);
                        }
                        await msg.AckAsync(cancellationToken: stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Chat fan-out failed; will redeliver.");
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
                logger.LogWarning(ex, "Chat fan-out consumer unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
