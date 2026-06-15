using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Chat;

/// <summary>
/// Visitor-facing chat REST surface: replaying a conversation's history when the
/// widget (re)opens. Realtime send/receive goes through <see cref="ChatHub"/>.
/// </summary>
public static class ChatEndpoints
{
    private const string PluginId = "live-chat";

    public static IEndpointRouteBuilder MapChatDelivery(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/{slug}/chat/conversations/{conversationId:guid}/messages", async (
            string slug, Guid conversationId, ITenantContext tenant,
            CmsDbContext cms, ChatDbContext chat, CancellationToken ct) =>
        {
            if (!await IsEnabled(cms, slug, ct) || tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }
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
        });

        return app;
    }

    private static async Task<bool> IsEnabled(CmsDbContext cms, string slug, CancellationToken ct)
        => await cms.PluginInstances.AsNoTracking()
            .AnyAsync(p => p.Slug == slug && p.PluginId == PluginId && p.Enabled, ct);
}
