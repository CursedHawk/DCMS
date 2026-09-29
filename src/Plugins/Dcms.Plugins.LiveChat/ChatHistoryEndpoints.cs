using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.LiveChat;

/// <summary>
/// Visitor-facing chat REST surface: replaying a conversation's history when the widget
/// (re)opens. Realtime send/receive goes through <see cref="ChatHub"/>.
/// </summary>
internal static class ChatHistoryEndpoints
{
    public static void Map(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapGet("/chat/conversations/{conversationId:guid}/messages", async (
            Guid conversationId, IPluginContext context, ChatDbContext chat, CancellationToken ct) =>
        {
            var tenantId = context.TenantId;
            // rls: request tenant. tenantId is the request's own, so the database already narrows
            // to it and no RlsScope is needed (ADR 0015).
            var owned = await chat.Conversations.IgnoreQueryFilters()
                .AnyAsync(c => c.Id == conversationId && c.TenantId == tenantId, ct);
            if (!owned)
            {
                return Results.NotFound();
            }
            var messages = await chat.Messages.IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && m.ConversationId == conversationId)
                .OrderBy(m => m.SentAt)
                .Select(m => new { id = m.Id, sender = m.Sender.ToString(), body = m.Body, sentAt = m.SentAt })
                .ToListAsync(ct);
            return Results.Ok(messages);
        }).PermissionExempt("The visitor's own conversation, addressed by its unguessable id; the same the widget holds.");
    }
}
