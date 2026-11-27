using System.Security.Claims;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.LiveChat;

/// <summary>
/// Agent console REST surface (admin plane): the conversation list and history, and answering and
/// closing. A reply reaches the visitor's widget over the plugin's WebSockets channel; open
/// consoles hear of every change as the <c>plugin:live-chat:conversations</c> hint and refetch.
/// Tenant is the ambient (header-resolved) tenant, so ChatDbContext's query
/// filter scopes every read automatically.
/// </summary>
internal static class ChatConsoleEndpoints
{
    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
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
        }).RequirePermission(PlatformPermissions.ChatRead)
          .Cli("chat.conversations", "The sites' chat conversations.", CliRisk.Read, externalText: true);

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
        }).RequirePermission(PlatformPermissions.ChatRead)
          .Cli("chat.messages", "One chat conversation's messages.", CliRisk.Read, externalText: true);

        app.MapPost("/api/admin/chat/conversations/{conversationId:guid}/messages", async (
            Guid conversationId, AgentReply body, HttpContext http, ITenantContext tenant, ILogger<LiveChatPlugin> logger, CancellationToken ct) =>
        {
            Guid? agent = Guid.TryParse(http.User.FindFirstValue("sub"), out var id) ? id : null;
            var sent = await ChatFlow.PostAsync(http.RequestServices, tenant.TenantId!.Value, conversationId, body.Body, ChatSender.Agent, agent, logger, ct);
            return sent is null ? Results.NotFound() : Results.Ok(sent.Dto);
        }).RequirePermission(PlatformPermissions.ChatManage)
          .AuditAs("reply.sent")
          .Cli("chat.reply", "Answer a chat conversation as an agent; the chatbot then stays quiet in it.", CliRisk.Safe);

        app.MapPost("/api/admin/chat/conversations/{conversationId:guid}/close", async (
            Guid conversationId, HttpContext http, ITenantContext tenant, ILogger<LiveChatPlugin> logger, CancellationToken ct) =>
            await ChatFlow.CloseAsync(http.RequestServices, tenant.TenantId!.Value, conversationId, logger, ct)
                ? Results.NoContent()
                : Results.NotFound())
          .RequirePermission(PlatformPermissions.ChatManage)
          .AuditAs("conversation.closed")
          .Cli("chat.close", "Close a chat conversation; the visitor can no longer write into it.", CliRisk.Safe);

        return app;
    }

    public sealed record AgentReply(string Body);
}
