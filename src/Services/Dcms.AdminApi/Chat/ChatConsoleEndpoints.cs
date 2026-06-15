using Dcms.Shared.Data.Chat;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Chat;

/// <summary>
/// Agent console REST surface. Realtime send/receive is over content-api's
/// SignalR hub; these endpoints back the conversation list and history panes.
/// Tenant is the ambient (header-resolved) tenant, so ChatDbContext's query
/// filter scopes every read automatically.
/// </summary>
public static class ChatConsoleEndpoints
{
    public static IEndpointRouteBuilder MapChatConsole(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/chat/conversations", async (
            string? status, ChatDbContext db, CancellationToken ct) =>
        {
            var query = db.Conversations.AsNoTracking();
            if (Enum.TryParse<ChatConversationStatus>(status, true, out var s))
            {
                query = query.Where(c => c.Status == s);
            }
            var conversations = await query
                .OrderByDescending(c => c.LastMessageAt)
                .Take(200)
                .Select(c => new
                {
                    id = c.Id,
                    visitorName = c.VisitorName,
                    status = c.Status.ToString(),
                    createdAt = c.CreatedAt,
                    lastMessageAt = c.LastMessageAt,
                })
                .ToListAsync(ct);
            return Results.Ok(conversations);
        }).RequirePermission(PlatformPermissions.ChatRead);

        app.MapGet("/api/admin/chat/conversations/{conversationId:guid}/messages", async (
            Guid conversationId, ChatDbContext db, CancellationToken ct) =>
        {
            var owned = await db.Conversations.AsNoTracking().AnyAsync(c => c.Id == conversationId, ct);
            if (!owned)
            {
                return Results.NotFound();
            }
            var messages = await db.Messages.AsNoTracking()
                .Where(m => m.ConversationId == conversationId)
                .OrderBy(m => m.SentAt)
                .Select(m => new { id = m.Id, sender = m.Sender.ToString(), body = m.Body, sentAt = m.SentAt, readAt = m.ReadAt })
                .ToListAsync(ct);
            return Results.Ok(messages);
        }).RequirePermission(PlatformPermissions.ChatRead);

        return app;
    }
}
