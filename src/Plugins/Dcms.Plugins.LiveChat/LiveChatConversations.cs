using Dcms.Plugins.LiveChat.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.LiveChat;

/// <summary><see cref="ILiveChat"/> over the chat schema; the caller's tenant is an explicit predicate.</summary>
internal sealed class LiveChatConversations(IPluginContext context, ChatDbContext db) : ILiveChat
{
    public async Task<ConversationList> ListAsync(ConversationQuery input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        var query = Conversations();
        if (Enum.TryParse<ChatConversationStatus>(input.Status, true, out var status))
        {
            query = query.Where(c => c.Status == status);
        }
        var rows = await query.OrderByDescending(c => c.LastMessageAt).Take(Math.Clamp(input.Limit, 1, 200))
            .Select(c => new { c.Id, c.VisitorName, c.Status, c.CreatedAt, c.LastMessageAt })
            .ToListAsync(ct);
        return new ConversationList(rows
            .Select(c => new ConversationSummary(c.Id, c.VisitorName, c.Status.ToString(), c.CreatedAt, c.LastMessageAt))
            .ToList());
    }

    public async Task<ConversationMessages> GetMessagesAsync(ConversationRef input, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(context.TenantId);
        if (!await Conversations().AnyAsync(c => c.Id == input.ConversationId, ct))
        {
            throw new ContractValidationException("No such conversation.");
        }
        var rows = await db.Messages.AsNoTracking().IgnoreQueryFilters()
            .Where(m => m.TenantId == context.TenantId && m.ConversationId == input.ConversationId)
            .OrderBy(m => m.SentAt)
            .Select(m => new { m.Id, m.Sender, m.Body, m.SentAt })
            .ToListAsync(ct);
        return new ConversationMessages(input.ConversationId,
            rows.Select(m => new ChatLine(m.Id, m.Sender.ToString(), m.Body, m.SentAt)).ToList());
    }

    private IQueryable<ChatConversation> Conversations() =>
        db.Conversations.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.TenantId == context.TenantId && !c.IsSandbox);
}

/// <summary>
/// A conversation started while nobody had the chat page open must not be lost: the tenant's
/// agents get it in the bell. Only the start notifies — every later line is realtime traffic to
/// whoever is looking, and one notification per line would make the bell useless.
/// </summary>
internal sealed class NotifyAgentsOfNewConversation : IPluginEventHandler<ChatConversationStarted>
{
    public const string Kind = "conversation-started";

    public Task HandleAsync(ChatConversationStarted e, IPluginContext context, CancellationToken ct) =>
        context.Contracts.Get<IPluginNotifications>().RaiseAsync(new NotificationRaise(
            Title: "New chat conversation",
            Body: "A visitor started a conversation and is waiting for an agent.",
            RequiredPermission: ChatPermissions.Read,
            // Keyed on the conversation, so a redelivery collapses onto one row.
            DedupeKey: $"conversation.started:{e.ConversationId:N}",
            LinkPath: "/app/live-chat/console",
            Kind: Kind,
            Params: new Dictionary<string, string> { ["visitor"] = e.VisitorName },
            ResourceType: "chat_conversation",
            ResourceId: e.ConversationId), ct);
}
