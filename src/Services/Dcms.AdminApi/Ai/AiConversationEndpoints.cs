using System.Text.Json.Nodes;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Ai;

/// <summary>
/// The assistant's stored conversations.
///
/// <para>The transcript used to live in React state, so a reload lost both the answer and the
/// reasoning behind whatever the agent had just written to the workspace. These endpoints give
/// it three lives instead: your own history, the conversations a member has chosen to share
/// with the workspace, and — for a holder of <c>ai:chats:read-all</c> — everything, because an
/// agent that can publish content is something a workspace owner should be able to review.</para>
///
/// <para>Turns are stored as the Anthropic content blocks the browser loop already speaks, so
/// resuming a conversation is the stored array handed back to the model unchanged. Which
/// provider serves it is ai-gateway's business, not this table's.</para>
/// </summary>
public static class AiConversationEndpoints
{
    /*
     * The permission that means "may use the assistant at all".
     *
     * It is site:edit because that is what `/api/admin/ai/messages` already requires — a member
     * who cannot reach the proxy cannot hold a conversation to store. Named once here so the two
     * move together if that gate is ever reconsidered; two different answers to "may I use the
     * assistant" is how you get a history page that lists chats the user can never continue.
     */
    private const string UsePermission = PlatformPermissions.SiteEdit;

    /// <summary>
    /// Per-append body cap. Tool results are stored whole — that is what makes a resumed
    /// conversation's detail cards work — but "whole" still needs a ceiling, or one runaway
    /// tool result becomes a row nobody can load.
    /// </summary>
    private const int MaxAppendBytes = 1024 * 1024;

    private const int MaxTitle = 160;

    public static IEndpointRouteBuilder MapAiConversationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/ai/conversations", async (
            string? scope, AiDbContext db, ITenantContext tenant, CurrentUser me,
            IPermissionResolver permissions, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var userId = me.RequireUserId();
            var query = db.Conversations.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.ArchivedAt == null);

            switch (scope)
            {
                case "workspace":
                    query = query.Where(c => c.Visibility == AiConversationVisibility.Workspace);
                    break;
                case "all":
                    if (!await CanReadAllAsync(permissions, me, tenantId, userId, ct))
                    {
                        return Results.Forbid();
                    }
                    break;
                default:
                    query = query.Where(c => c.OwnerUserId == userId);
                    break;
            }

            var rows = await query
                .OrderByDescending(c => c.UpdatedAt)
                .Take(200)
                .Select(c => new ConversationSummary(
                    c.Id, c.Title, c.Visibility.ToString(), c.Mode, c.PageArea,
                    c.MessageCount, c.OwnerUserId, c.OwnerUserId == userId, c.CreatedAt, c.UpdatedAt))
                .ToListAsync(ct);

            return Results.Ok(rows);
        }).RequirePermission(UsePermission);

        app.MapGet("/api/admin/ai/conversations/{id:guid}", async (
            Guid id, AiDbContext db, ITenantContext tenant, CurrentUser me,
            IPermissionResolver permissions, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var userId = me.RequireUserId();
            var conversation = await db.Conversations.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);

            // A conversation the caller may not read is reported as absent rather than refused:
            // "you are not allowed to read this" still tells them it exists and who else is
            // talking to the assistant.
            if (conversation is null || !await CanReadAsync(conversation, permissions, me, tenantId, userId, ct))
            {
                return Results.NotFound();
            }

            var messages = await db.Messages.AsNoTracking()
                .Where(m => m.ConversationId == id && m.TenantId == tenantId)
                .OrderBy(m => m.Seq)
                .Select(m => new { m.Id, m.Seq, m.Role, m.Content, m.CreatedAt })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                id = conversation.Id,
                title = conversation.Title,
                visibility = conversation.Visibility.ToString(),
                mode = conversation.Mode,
                pageArea = conversation.PageArea,
                messageCount = conversation.MessageCount,
                ownerUserId = conversation.OwnerUserId,
                mine = conversation.OwnerUserId == userId,
                createdAt = conversation.CreatedAt,
                updatedAt = conversation.UpdatedAt,
                // Parsed back to JSON rather than handed over as a string: the browser holds
                // these blocks in the shape the model produced them, not a quoted copy of it.
                messages = messages.Select(m => new
                {
                    m.Id,
                    m.Seq,
                    m.Role,
                    content = JsonNode.Parse(m.Content),
                    m.CreatedAt,
                }),
            });
        }).RequirePermission(UsePermission);

        app.MapPost("/api/admin/ai/conversations", async (
            CreateConversationRequest body, AiDbContext db, ITenantContext tenant, CurrentUser me,
            CancellationToken ct) =>
        {
            var conversation = new AiConversation
            {
                TenantId = tenant.TenantId!.Value,
                OwnerUserId = me.RequireUserId(),
                Title = Clamp(body.Title, MaxTitle, "New conversation"),
                Mode = StorableMode(body.Mode),
                PageArea = Clamp(body.PageArea, 64, null),
            };

            db.Conversations.Add(conversation);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id = conversation.Id, title = conversation.Title });
        })
        .RequirePermission(UsePermission)
        .AuditExempt("Opening a scratch conversation changes nothing in the workspace; the turns "
                     + "inside it are recorded by ai.request, and anything it writes by that write's own action.");

        app.MapPost("/api/admin/ai/conversations/{id:guid}/messages", async (
            Guid id, AppendMessagesRequest body, AiDbContext db, ITenantContext tenant, CurrentUser me,
            CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var userId = me.RequireUserId();

            // Only the owner writes. A read-all holder reviews conversations; they do not
            // continue somebody else's on their behalf.
            var conversation = await db.Conversations
                .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId && c.OwnerUserId == userId, ct);
            if (conversation is null) return Results.NotFound();

            if (body.Messages is not { Count: > 0 })
            {
                return Results.BadRequest(new { error = "At least one message is required." });
            }

            var next = await db.Messages
                .Where(m => m.ConversationId == id)
                .Select(m => (int?)m.Seq)
                .MaxAsync(ct) ?? 0;

            var total = 0;
            foreach (var message in body.Messages)
            {
                if (message.Role is not ("user" or "assistant"))
                {
                    return Results.BadRequest(new { error = $"Unknown role \"{message.Role}\"." });
                }

                var json = message.Content?.ToJsonString() ?? "[]";
                total += json.Length;
                if (total > MaxAppendBytes)
                {
                    return Results.BadRequest(new
                    {
                        error = "This turn is too large to store. Start a new conversation.",
                    });
                }

                db.Messages.Add(new AiMessage
                {
                    ConversationId = id,
                    TenantId = tenantId,
                    Seq = ++next,
                    Role = message.Role,
                    Content = json,
                });
            }

            conversation.MessageCount = next;
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(body.Title)) conversation.Title = Clamp(body.Title, MaxTitle, conversation.Title);
            if (!string.IsNullOrWhiteSpace(body.Mode)) conversation.Mode = StorableMode(body.Mode);

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { messageCount = conversation.MessageCount });
        })
        .RequirePermission(UsePermission)
        .AuditExempt("One record per turn would bury the log; ai.request already names every "
                     + "model call, and each change the agent makes carries its own action.");

        app.MapPatch("/api/admin/ai/conversations/{id:guid}", async (
            Guid id, UpdateConversationRequest body, AiDbContext db, ITenantContext tenant,
            CurrentUser me, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var conversation = await db.Conversations.FirstOrDefaultAsync(
                c => c.Id == id && c.TenantId == tenantId && c.OwnerUserId == me.RequireUserId(), ct);
            if (conversation is null) return Results.NotFound();

            if (body.Title is not null) conversation.Title = Clamp(body.Title, MaxTitle, conversation.Title);
            if (body.Mode is not null) conversation.Mode = StorableMode(body.Mode);
            if (body.Visibility is not null)
            {
                if (!Enum.TryParse<AiConversationVisibility>(body.Visibility, ignoreCase: true, out var visibility))
                {
                    return Results.BadRequest(new { error = "Visibility must be private or workspace." });
                }
                conversation.Visibility = visibility;
            }
            if (body.Archived is true) conversation.ArchivedAt = DateTimeOffset.UtcNow;
            else if (body.Archived is false) conversation.ArchivedAt = null;

            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // The audit entry's subject is the route's {id}; the field diff comes off the same
            // SaveChanges above, so the record already says private -> workspace by itself.
            return Results.Ok(new
            {
                id = conversation.Id,
                title = conversation.Title,
                visibility = conversation.Visibility.ToString(),
                archived = conversation.ArchivedAt is not null,
            });
        })
        .RequirePermission(UsePermission)
        .WithAudit(AuditActions.AiConversationUpdated, "ai_conversation");

        app.MapDelete("/api/admin/ai/conversations/{id:guid}", async (
            Guid id, AiDbContext db, ITenantContext tenant, CurrentUser me, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var conversation = await db.Conversations.FirstOrDefaultAsync(
                c => c.Id == id && c.TenantId == tenantId && c.OwnerUserId == me.RequireUserId(), ct);
            if (conversation is null) return Results.NotFound();

            // Tracked delete rather than ExecuteDelete: the audit entry is buffered before the
            // handler runs and rides the handler's own SaveChanges into the same commit, which
            // ExecuteDelete would bypass. The messages go with it on the database's cascade.
            db.Conversations.Remove(conversation);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .RequirePermission(UsePermission)
        .WithAudit(AuditActions.AiConversationDeleted, "ai_conversation");

        return app;
    }

    private static async Task<bool> CanReadAsync(
        AiConversation conversation, IPermissionResolver permissions, CurrentUser me,
        Guid tenantId, Guid userId, CancellationToken ct) =>
        conversation.OwnerUserId == userId
        || conversation.Visibility == AiConversationVisibility.Workspace
        || await CanReadAllAsync(permissions, me, tenantId, userId, ct);

    private static async Task<bool> CanReadAllAsync(
        IPermissionResolver permissions, CurrentUser me, Guid tenantId, Guid userId, CancellationToken ct)
    {
        if (me.IsSuperAdmin) return true;
        var held = await permissions.GetPermissionsAsync(tenantId, userId, ct);
        return held.Contains(PlatformPermissions.AiChatsReadAll);
    }

    /// <summary>
    /// The mode as it is allowed to persist.
    ///
    /// <para><c>auto</c> is never stored. Full auto runs dangerous tools — publishing, deleting —
    /// with nobody in the loop, and a posture like that should be something the operator chose
    /// this session, not something a conversation quietly restores a fortnight later. Anything
    /// unrecognised falls back to <c>agent</c>, the default.</para>
    /// </summary>
    private static string StorableMode(string? mode) => mode switch
    {
        "read" or "careful" or "agent" => mode,
        _ => "agent",
    };

    private static string Clamp(string? value, int max, string? fallback)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return fallback ?? string.Empty;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private sealed record ConversationSummary(
        Guid Id, string Title, string Visibility, string Mode, string? PageArea,
        int MessageCount, Guid OwnerUserId, bool Mine, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private sealed record CreateConversationRequest(string? Title, string? Mode, string? PageArea);

    private sealed record UpdateConversationRequest(
        string? Title, string? Visibility, string? Mode, bool? Archived);

    private sealed record AppendMessagesRequest(
        List<AppendMessage>? Messages, string? Title, string? Mode);

    private sealed record AppendMessage(string Role, JsonArray? Content);
}
