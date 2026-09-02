using Dcms.AdminApi.Notifications;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.AdminApi.Chat;

/// <summary>
/// Durable consumer of the CHAT stream. Realtime delivery to connected agents is
/// already handled by content-api's SignalR Redis backplane; this consumer is the
/// out-of-band fan-out point for offline-agent notifications, which is what it now does:
/// a visitor opening a conversation raises an in-app notification for the tenant's agents,
/// so a conversation started while nobody had the chat page open is not lost.
///
/// <para>Only the first message of a conversation notifies. Every subsequent message is
/// realtime traffic to whoever is looking, and one notification per chat line would make the
/// bell useless within a single conversation.</para>
///
/// <para>Resilient to NATS being unavailable.</para>
/// </summary>
public sealed class ChatFanoutConsumer(
    IServiceProvider services,
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
                            await NotifyAsync(e, stoppingToken);
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

    private async Task NotifyAsync(ChatMessagePosted e, CancellationToken ct)
    {
        // Agent and bot replies are not news to the agents.
        if (!string.Equals(e.Sender, "Visitor", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var scope = services.CreateScope();
        var chat = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        // Cross-tenant consumer with no ambient tenant or sandbox, so both filter terms are
        // named explicitly. Sandbox conversations come from site previews and are test data.
        var conversation = await chat.Conversations.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.TenantId == e.TenantId && c.Id == e.ConversationId)
            .Select(c => new { c.VisitorName, c.IsSandbox })
            .FirstOrDefaultAsync(ct);

        if (conversation is null || conversation.IsSandbox)
        {
            return;
        }

        var isFirstMessage = await chat.Messages.AsNoTracking().IgnoreQueryFilters()
            .Where(m => m.TenantId == e.TenantId && m.ConversationId == e.ConversationId)
            .CountAsync(ct) <= 1;

        if (!isFirstMessage)
        {
            return;
        }

        var publisher = scope.ServiceProvider.GetRequiredService<INotificationPublisher>();
        await publisher.RaiseAsync(new NotificationRequest(
            TenantId: e.TenantId,
            Kind: NotificationKinds.ChatConversationStarted,
            Severity: NotificationSeverity.Info,
            RequiredPermission: PlatformPermissions.ChatRead,
            TitleKey: NotificationKinds.TitleKey(NotificationKinds.ChatConversationStarted),
            BodyKey: NotificationKinds.BodyKey(NotificationKinds.ChatConversationStarted),
            // Keyed on the conversation, not the message: if the "is this the first message"
            // check races with a second arriving message, the unique index collapses the two.
            DedupeKey: $"chat.conversation.started:{e.ConversationId:N}",
            Params: new { visitor = conversation.VisitorName },
            LinkPath: "/chat",
            ResourceType: "chat_conversation",
            ResourceId: e.ConversationId), ct);
    }
}
