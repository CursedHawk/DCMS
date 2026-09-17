using System.Security.Claims;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Security;
using Dcms.Shared.Messaging;
using Dcms.Shared.Telemetry;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Chat;

/// <summary>
/// Live-chat hub shared by website visitors and tenant agents. Cross-replica
/// fan-out is handled by the Redis backplane; the per-conversation and per-tenant
/// SignalR groups partition delivery. A <c>chat.message.posted</c> NATS event is
/// also emitted for admin-api's out-of-band fan-out (notifications/unread counts).
///
/// Tenant is resolved from the <c>?tenant={slug}</c> query string at connect time
/// (the SignalR WebSocket transport can't carry custom headers), so DB access uses
/// explicit tenant predicates rather than the ambient query filter.
/// </summary>
public sealed class ChatHub(
    IServiceProvider services,
    IEventPublisher events,
    ChatBotResponder botResponder,
    DcmsMetrics metrics,
    ILogger<ChatHub> logger) : Hub
{
    private const string TenantItemKey = "dcms.tenantId";
    private const string RoleItemKey = "dcms.role";
    private const string ManageItemKey = "dcms.chatManage";

    // Public so the out-of-band bot responder can fan replies into the same groups.
    public static string ConversationGroup(Guid conversationId) => $"conv:{conversationId}";
    public static string AgentGroup(Guid tenantId) => $"agents:{tenantId}";

    private Guid TenantId => (Guid)Context.Items[TenantItemKey]!;
    private bool IsAgent => Context.Items.TryGetValue(RoleItemKey, out var role) && (string?)role == "agent";
    private bool CanManageChat => Context.Items.TryGetValue(ManageItemKey, out var m) && m is true;
    private Guid? UserId => Guid.TryParse(Context.User?.FindFirstValue("sub"), out var id) ? id : null;

    /// <summary>The caller's effective permission strings in the tenant, mirroring admin-api's
    /// resolver (content-api has no permission cache of its own).</summary>
    private static async Task<IReadOnlySet<string>> ResolvePermissionsAsync(
        IServiceScope scope, Guid tenantId, Guid userId)
    {
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var roleIds = await tenancy.Memberships.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.UserId == userId)
            .SelectMany(m => m.Roles.Select(r => r.TenantRoleId))
            .ToListAsync();
        if (roleIds.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        var perms = await tenancy.TenantRolePermissions.IgnoreQueryFilters()
            .Where(p => roleIds.Contains(p.TenantRoleId))
            .Select(p => p.Permission)
            .ToListAsync();
        return perms.ToHashSet(StringComparer.Ordinal);
    }

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var slug = http?.Request.Query["tenant"].ToString();
        if (string.IsNullOrWhiteSpace(slug))
        {
            Context.Abort();
            return;
        }

        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<TenantStore>();
        var tenant = await store.GetByIdentifierAsync(slug);
        if (tenant is null || !Guid.TryParse(tenant.Id, out var tenantId))
        {
            Context.Abort();
            return;
        }
        Context.Items[TenantItemKey] = tenantId;

        // SEC-08: an agent is not merely any member of the tenant — visitor conversations may
        // hold personal data, so joining the agent group requires chat:read, and speaking or
        // closing conversations requires chat:manage. A SuperAdmin holds both.
        if (Context.User?.Identity?.IsAuthenticated == true && UserId is { } userId)
        {
            var isSuperAdmin = Context.User.FindAll("role").Any(c => c.Value == "SuperAdmin");
            var perms = isSuperAdmin
                ? null // unrestricted
                : await ResolvePermissionsAsync(scope, tenantId, userId);

            var canRead = isSuperAdmin
                || (perms is not null && perms.Contains(PlatformPermissions.ChatRead));
            var canManage = isSuperAdmin
                || (perms is not null && perms.Contains(PlatformPermissions.ChatManage));

            if (canRead)
            {
                Context.Items[RoleItemKey] = "agent";
                Context.Items[ManageItemKey] = canManage;
                await Groups.AddToGroupAsync(Context.ConnectionId, AgentGroup(tenantId));
            }
        }

        await base.OnConnectedAsync();
    }

    /// <summary>Visitor: opens a new conversation and subscribes to it.</summary>
    public async Task<object> StartConversation(string? visitorName)
    {
        var tenantId = TenantId;

        // SEC-09: cap how many conversations one connection may open, so a script cannot spin up
        // unbounded conversations (each of which then admits its own message rate).
        if (!IsAgent)
        {
            var opened = Context.Items.TryGetValue(ConvCountKey, out var n) && n is int ni ? ni : 0;
            if (opened >= MaxConversationsPerConnection)
            {
                throw new HubException("Too many conversations from this session.");
            }
            Context.Items[ConvCountKey] = opened + 1;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        var conversation = new ChatConversation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            VisitorId = IsAgent ? null : UserId,
            VisitorName = string.IsNullOrWhiteSpace(visitorName) ? "Visitor" : visitorName.Trim(),
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversation.Id));
        await Clients.Group(AgentGroup(tenantId)).SendAsync("ConversationStarted", new
        {
            id = conversation.Id,
            visitorName = conversation.VisitorName,
            status = conversation.Status.ToString(),
            lastMessageAt = conversation.LastMessageAt,
        });

        return new { id = conversation.Id, visitorName = conversation.VisitorName };
    }

    /// <summary>Subscribe an existing connection to a conversation's group.</summary>
    public async Task JoinConversation(Guid conversationId)
    {
        var tenantId = TenantId;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var exists = await db.Conversations.IgnoreQueryFilters()
            .AnyAsync(c => c.Id == conversationId && c.TenantId == tenantId);
        if (!exists)
        {
            throw new HubException("Conversation not found.");
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));
    }

    // SEC-09: each visitor message can schedule a tenant-billed AI reply, and the WebSocket
    // transport is exempt from the edge's rate limiter, so the hub throttles per connection.
    // This bounds the AI calls one connection can trigger; the ai-gateway per-tenant quota is
    // still the aggregate backstop.
    private const int VisitorMessagesPerWindow = 20;
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);
    private const int MaxConversationsPerConnection = 10;
    private const string RateWindowKey = "dcms.rate.window";
    private const string RateCountKey = "dcms.rate.count";
    private const string ConvCountKey = "dcms.conv.count";

    /// <summary>Visitor sends a message into a conversation.</summary>
    public Task SendMessage(Guid conversationId, string body)
    {
        // Agents don't trigger the bot and aren't the abuse vector.
        if (!IsAgent)
        {
            var now = DateTimeOffset.UtcNow;
            var windowStart = Context.Items.TryGetValue(RateWindowKey, out var w) && w is DateTimeOffset dt ? dt : now;
            var count = Context.Items.TryGetValue(RateCountKey, out var c) && c is int ci ? ci : 0;
            if (now - windowStart > RateWindow)
            {
                windowStart = now;
                count = 0;
            }
            count++;
            Context.Items[RateWindowKey] = windowStart;
            Context.Items[RateCountKey] = count;
            if (count > VisitorMessagesPerWindow)
            {
                throw new HubException("You're sending messages too quickly. Please wait a moment.");
            }
        }
        return PostAsync(conversationId, body, ChatSender.Visitor);
    }

    /// <summary>Agent replies into a conversation. Requires an authenticated tenant member.</summary>
    public Task SendAgentMessage(Guid conversationId, string body)
    {
        if (!CanManageChat)
        {
            throw new HubException("Not authorized to answer chats for this tenant (chat:manage).");
        }
        return PostAsync(conversationId, body, ChatSender.Agent);
    }

    public async Task CloseConversation(Guid conversationId)
    {
        if (!CanManageChat)
        {
            throw new HubException("Not authorized to close chats for this tenant (chat:manage).");
        }
        var tenantId = TenantId;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var conversation = await db.Conversations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.TenantId == tenantId);
        if (conversation is null)
        {
            return;
        }
        conversation.Status = ChatConversationStatus.Closed;
        await db.SaveChangesAsync();
        await Clients.Group(ConversationGroup(conversationId)).SendAsync("ConversationClosed", conversationId);
    }

    private async Task PostAsync(Guid conversationId, string body, ChatSender sender)
    {
        body = body?.Trim() ?? string.Empty;
        if (body.Length == 0)
        {
            return;
        }
        if (body.Length > 8000)
        {
            body = body[..8000];
        }

        var tenantId = TenantId;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        var conversation = await db.Conversations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.TenantId == tenantId);
        if (conversation is null || conversation.Status == ChatConversationStatus.Closed)
        {
            throw new HubException("Conversation is not available.");
        }

        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conversationId,
            Sender = sender,
            SenderId = sender == ChatSender.Agent ? UserId : conversation.VisitorId,
            Body = body,
            SentAt = now,
        };
        db.Messages.Add(message);
        conversation.LastMessageAt = now;
        await db.SaveChangesAsync();

        // Visitor vs Agent, which is the ratio the chat dashboard is actually for: a tenant
        // whose agents send nothing has a widget nobody answers. Bot replies are counted by
        // ChatBotResponder, on the same meter, so the three add up.
        metrics.ChatMessage(tenantId, sender.ToString());

        var dto = new
        {
            id = message.Id,
            conversationId,
            sender = sender.ToString(),
            body,
            sentAt = now,
        };
        await Clients.Group(ConversationGroup(conversationId)).SendAsync("ReceiveMessage", dto);
        // Surface visitor messages to any agent who hasn't joined the conversation yet,
        // and let the AI assistant answer (unless a human agent has taken over).
        if (sender == ChatSender.Visitor)
        {
            await Clients.Group(AgentGroup(tenantId)).SendAsync("ConversationActivity", new
            {
                id = conversationId,
                visitorName = conversation.VisitorName,
                lastMessageAt = now,
                preview = body.Length > 120 ? body[..120] : body,
            });

            botResponder.Trigger(tenantId, conversationId, body);
        }

        try
        {
            await events.PublishAsync(Subjects.ChatMessagePosted, new ChatMessagePosted(
                Guid.NewGuid(), now, tenantId, conversationId, message.Id, sender.ToString()));
        }
        catch (Exception ex)
        {
            // Realtime delivery already succeeded; the NATS fan-out is best-effort.
            logger.LogWarning(ex, "chat.message.posted publish failed (delivery already sent).");
        }
    }
}
