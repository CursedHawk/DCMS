using System.Collections.Concurrent;
using System.Text.Json;
using Dcms.Plugins.LiveChat.Api;
using Dcms.Plugins.WebSockets.Api;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.LiveChat;

/// <summary>
/// The live chat's realtime side (ADR 0037, #669): the plugin's own WebSockets channel set. A
/// visitor's widget subscribes to <c>conversation/{id}</c> at <c>/hub/ws?instance={live-chat app}</c>
/// and only listens; every write is an HTTP request. No WebSockets app of the tenant's is needed.
/// </summary>
internal static class ChatChannels
{
    public const string Conversation = "conversation";

    public static readonly WebSocketChannelSet Set = new(LiveChatPlugin.PluginId,
    [
        new ChannelDefinition(Conversation,
            Description: "One conversation's new messages and its closing, keyed by the conversation id.",
            Keyed: true, Subscribe: SubscribeAccess.Public, Publish: PublishAccess.None),
    ]);
}

/// <summary>
/// Everything that writes a conversation, for every caller: the visitor's routes on the site
/// plane, the agents' console routes on the admin plane and the chatbot. A write is stored first,
/// then pushed to the visitor's widget (the conversation channel, over the WebSockets backplane
/// from either plane), then hinted to open consoles (<c>plugin:live-chat:conversations</c> on the
/// notification hub), then published as the plugin's events. Each step after the store is best
/// effort: realtime delivery and a bus problem must never cost a chat line.
/// </summary>
internal static class ChatFlow
{
    public const string ConsoleTag = "conversations";
    public const int MaxBody = 8000;

    // SEC-09: each visitor message can schedule a tenant-billed AI reply. The HTTP routes are behind
    // the edge's rate limiter, and these bound what one address and one conversation can cost.
    // ponytail: per replica, in memory; move both to Redis when content-api runs replicas.
    private const int VisitorMessagesPerMinute = 20;
    private const int ConversationsPerAddressPerHour = 20;
    private static readonly ConcurrentDictionary<Guid, Window> MessageWindows = new();
    private static readonly ConcurrentDictionary<string, Window> ConversationWindows = new(StringComparer.Ordinal);

    private sealed class Window(DateTimeOffset start)
    {
        public DateTimeOffset Start = start;
        public int Count;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>False when this address opened too many conversations within the hour.</summary>
    public static bool AdmitConversation(string address) => Admit(ConversationWindows, address, TimeSpan.FromHours(1), ConversationsPerAddressPerHour);

    /// <summary>False when the visitor writes too quickly into this conversation.</summary>
    public static bool AdmitVisitorMessage(Guid conversationId) => Admit(MessageWindows, conversationId, TimeSpan.FromMinutes(1), VisitorMessagesPerMinute);

    private static bool Admit<TKey>(ConcurrentDictionary<TKey, Window> windows, TKey key, TimeSpan span, int max) where TKey : notnull
    {
        var now = DateTimeOffset.UtcNow;
        if (windows.Count > 50_000)
        {
            // Old windows only matter while they are open.
            foreach (var (k, w) in windows)
            {
                if (now - w.Start > span) windows.TryRemove(k, out _);
            }
        }
        var window = windows.GetOrAdd(key, _ => new Window(now));
        lock (window)
        {
            if (now - window.Start > span)
            {
                window.Start = now;
                window.Count = 0;
            }
            return ++window.Count <= max;
        }
    }

    public static async Task<ChatConversation> StartAsync(IServiceProvider sp, Guid tenantId, Guid? visitorId, string? visitorName, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ChatDbContext>();
        var name = visitorName?.Trim();
        var conversation = new ChatConversation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            VisitorId = visitorId,
            VisitorName = string.IsNullOrEmpty(name) ? "Visitor" : name[..Math.Min(name.Length, 100)],
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(ct);
        await SignalConsolesAsync(sp, tenantId, conversation.Id, ct);
        return conversation;
    }

    /// <summary>The conversation of this request's tenant (and environment), or null.</summary>
    public static Task<ChatConversation?> FindAsync(IServiceProvider sp, Guid tenantId, Guid conversationId, CancellationToken ct) =>
        // rls: request tenant. The caller's own tenant, which the database already narrows to.
        sp.GetRequiredService<ChatDbContext>().Conversations.IgnoreTenantFilter()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.TenantId == tenantId, ct);

    /// <summary>A stored message as every client sees it, and the environment of its conversation.</summary>
    public sealed record Posted(object Dto, Guid EnvironmentId);

    /// <summary>Stores a message and delivers it; null when the conversation is gone or closed.</summary>
    public static async Task<Posted?> PostAsync(
        IServiceProvider sp, Guid tenantId, Guid conversationId, string? body, ChatSender sender, Guid? senderId,
        ILogger logger, CancellationToken ct)
    {
        body = body?.Trim() ?? "";
        if (body.Length == 0)
        {
            return null;
        }
        if (body.Length > MaxBody)
        {
            body = body[..MaxBody];
        }

        var db = sp.GetRequiredService<ChatDbContext>();
        var conversation = await FindAsync(sp, tenantId, conversationId, ct);
        if (conversation is null || conversation.Status == ChatConversationStatus.Closed)
        {
            return null;
        }
        // rls: request tenant (or the bot's environment scope), as above.
        var firstFromVisitor = sender == ChatSender.Visitor && !await db.Messages.IgnoreTenantFilter()
            .AnyAsync(m => m.TenantId == tenantId && m.ConversationId == conversationId && m.Sender == ChatSender.Visitor, ct);

        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            // The conversation's, whichever environment the console that answers is looking at.
            EnvironmentId = conversation.EnvironmentId,
            ConversationId = conversationId,
            Sender = sender,
            SenderId = senderId ?? (sender == ChatSender.Visitor ? conversation.VisitorId : null),
            Body = body,
            SentAt = now,
        };
        db.Messages.Add(message);
        conversation.LastMessageAt = now;
        await db.SaveChangesAsync(ct);

        // Visitor, Agent and Bot on one meter: the ratio the chat dashboard is for.
        sp.GetRequiredService<DcmsMetrics>().ChatMessage(tenantId, sender.ToString());

        var dto = new { kind = "message", id = message.Id, conversationId, sender = sender.ToString(), body, sentAt = now };
        await PushAsync(sp, tenantId, conversation.EnvironmentId, conversationId, dto, logger, ct);
        await SignalConsolesAsync(sp, tenantId, conversationId, ct);
        await PublishAsync(sp, tenantId, logger,
            firstFromVisitor ? new ChatConversationStarted(conversationId, conversation.VisitorName) : null,
            new ChatMessageReceived(conversationId, message.Id, sender.ToString()));
        return new Posted(dto, conversation.EnvironmentId);
    }

    public static async Task<bool> CloseAsync(IServiceProvider sp, Guid tenantId, Guid conversationId, ILogger logger, CancellationToken ct)
    {
        var conversation = await FindAsync(sp, tenantId, conversationId, ct);
        if (conversation is null)
        {
            return false;
        }
        conversation.Status = ChatConversationStatus.Closed;
        await sp.GetRequiredService<ChatDbContext>().SaveChangesAsync(ct);
        await PushAsync(sp, tenantId, conversation.EnvironmentId, conversationId, new { kind = "closed", conversationId }, logger, ct);
        await SignalConsolesAsync(sp, tenantId, conversationId, ct);
        return true;
    }

    /// <summary>To the visitor's widget: every live-chat app of the conversation's environment.</summary>
    private static async Task PushAsync(IServiceProvider sp, Guid tenantId, Guid environmentId, Guid conversationId, object payload,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            var push = sp.GetRequiredService<IWebSocketPush>();
            var json = JsonSerializer.SerializeToElement(payload, Web);
            var apps = (await sp.GetRequiredService<PluginContextFactory>().EnabledInstancesAsync(tenantId, ct))
                .Where(i => i.PluginId == LiveChatPlugin.PluginId && i.EnvironmentId == environmentId);
            foreach (var app in apps)
            {
                await push.SendAsync(LiveChatPlugin.PluginId, tenantId, environmentId, app.Slug,
                    new ChannelMessage(ChatChannels.Conversation, json, conversationId.ToString("N")), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Pushing to live chat conversation {ConversationId} failed (the message is stored).", conversationId);
        }
    }

    /// <summary>
    /// Open consoles refetch the conversation list and an open conversation. The hint reaches every
    /// console of the tenant, so it carries neither text nor the conversation id: the id is what
    /// the visitor's routes accept, and a member without chat:read must not learn one.
    /// </summary>
    private static async Task SignalConsolesAsync(IServiceProvider sp, Guid tenantId, Guid conversationId, CancellationToken ct)
    {
        try
        {
            var plugin = await sp.GetRequiredService<PluginContextFactory>()
                .CreateAsync(tenantId, LiveChatPlugin.PluginId, null, PluginActor.Anonymous, ct);
            await plugin.Contracts.Get<IPluginNotifications>().SignalChangeAsync(new ConsoleChange(ConsoleTag), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ChatFlow))
                .LogDebug(ex, "Hinting consoles about conversation {ConversationId} failed.", conversationId);
        }
    }

    /// <summary>The plugin's events, after the store. Best effort, like the push.</summary>
    internal static async Task PublishAsync(IServiceProvider sp, Guid tenantId, ILogger logger, params IPluginEvent?[] events)
    {
        try
        {
            var plugin = await sp.GetRequiredService<PluginContextFactory>()
                .CreateAsync(tenantId, LiveChatPlugin.PluginId, null, PluginActor.Anonymous, CancellationToken.None);
            foreach (var e in events.OfType<IPluginEvent>())
            {
                await plugin.PublishAsync(e, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Publishing live-chat events failed (delivery already sent).");
        }
    }
}
