using System.Security.Claims;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.LiveChat;

/// <summary>
/// The visitor's side of the chat, on the site plane under <c>/api/{slug}/chat</c>: open a
/// conversation, write into it, replay it. Everything the widget hears back arrives on the
/// plugin's WebSockets channel <c>conversation/{id}</c> (<see cref="ChatChannels"/>). A
/// conversation is addressed by its unguessable id, the one the widget holds.
/// </summary>
internal static class ChatSiteEndpoints
{
    public sealed record StartBody(string? VisitorName = null);

    public sealed record MessageBody(string Body);

    public static void Map(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapPost("/chat/conversations", async (
            StartBody? body, HttpContext http, IPluginContext context, CancellationToken ct) =>
        {
            if (!ChatFlow.AdmitConversation(http.Connection.RemoteIpAddress?.ToString() ?? "unknown"))
            {
                return Results.Problem("Too many conversations from this address; try again later.", statusCode: StatusCodes.Status429TooManyRequests);
            }
            Guid? visitor = Guid.TryParse(http.User.FindFirstValue("sub"), out var id) ? id : null;
            var conversation = await ChatFlow.StartAsync(http.RequestServices, context.TenantId, visitor, body?.VisitorName, ct);
            return Results.Ok(new { id = conversation.Id, visitorName = conversation.VisitorName });
        }).PermissionExempt("Anonymous by design: any visitor of the site may open a conversation; capped per address.")
          .AuditAs("conversation.started");

        endpoints.MapPost("/chat/conversations/{conversationId:guid}/messages", async (
            Guid conversationId, MessageBody body, HttpContext http, IPluginContext context,
            ChatBotResponder bot, ILogger<LiveChatPlugin> logger, CancellationToken ct) =>
        {
            if (!ChatFlow.AdmitVisitorMessage(conversationId))
            {
                return Results.Problem("You're sending messages too quickly. Please wait a moment.", statusCode: StatusCodes.Status429TooManyRequests);
            }
            var sent = await ChatFlow.PostAsync(http.RequestServices, context.TenantId, conversationId, body.Body, ChatSender.Visitor, null, logger, ct);
            if (sent is null)
            {
                return Results.NotFound();
            }
            // The assistant answers, unless a human agent has taken the conversation over.
            bot.Trigger(context.TenantId, sent.EnvironmentId, conversationId, body.Body.Trim());
            return Results.Ok(sent.Dto);
        }).PermissionExempt("The visitor's own conversation, addressed by its unguessable id; capped per conversation.")
          .SkipAudit("Each message is the stored chat line itself; a record per line would only copy the transcript.");

        endpoints.MapGet("/chat/conversations/{conversationId:guid}/messages", async (
            Guid conversationId, IPluginContext context, ChatDbContext chat, CancellationToken ct) =>
        {
            var tenantId = context.TenantId;
            // rls: request tenant. tenantId is the request's own, so the database already narrows
            // to it and no RlsScope is needed (ADR 0015).
            var owned = await chat.Conversations.IgnoreTenantFilter()
                .AnyAsync(c => c.Id == conversationId && c.TenantId == tenantId, ct);
            if (!owned)
            {
                return Results.NotFound();
            }
            var messages = await chat.Messages.IgnoreTenantFilter()
                .Where(m => m.TenantId == tenantId && m.ConversationId == conversationId)
                .OrderBy(m => m.SentAt)
                .Select(m => new { id = m.Id, sender = m.Sender.ToString(), body = m.Body, sentAt = m.SentAt })
                .ToListAsync(ct);
            return Results.Ok(messages);
        }).PermissionExempt("The visitor's own conversation, addressed by its unguessable id; the same the widget holds.");
    }
}
