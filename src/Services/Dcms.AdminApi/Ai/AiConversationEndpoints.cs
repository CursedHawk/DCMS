using System.Text.Json.Nodes;
using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Ai;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Dcms.Shared.Telemetry;
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

    /// <summary>
    /// Cap on one run record's JSON. Far smaller than the per-turn cap because a run stores
    /// summaries — changed paths, a validation report, token totals — and never the edits
    /// themselves, which are already in the turns that made them.
    /// </summary>
    private const int MaxRunBytes = 64 * 1024;

    private const int MaxRunTask = 2000;

    public static IEndpointRouteBuilder MapAiConversationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/ai/conversations", async (
            string? scope, string? surface, Guid? siteId, AiDbContext db, ITenantContext tenant,
            CurrentUser me, IPermissionResolver permissions, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var userId = me.RequireUserId();
            var query = db.Conversations.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.ArchivedAt == null);

            /*
             * Surface is what keeps one table usable by two rails.
             *
             * The IDE produces many short runs and the console a few long conversations; listed
             * together the second disappears into the first. An unnamed surface means "the
             * console", because that is what every row predating the column is and what an
             * older client asking this endpoint means.
             */
            var wanted = AiSurfaces.Normalise(surface);
            query = query.Where(c => c.Surface == wanted);

            // A site filter only narrows within the IDE surface; a console conversation has no
            // site, and silently returning none for one would look like a broken rail.
            if (siteId is { } site && wanted == AiSurfaces.Ide)
            {
                query = query.Where(c => c.SiteId == site);
            }

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
                    c.Surface, c.SiteId, c.Branch,
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

            var runs = await db.Runs.AsNoTracking()
                .Where(r => r.ConversationId == id && r.TenantId == tenantId)
                .OrderBy(r => r.StartedAt)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                id = conversation.Id,
                title = conversation.Title,
                visibility = conversation.Visibility.ToString(),
                mode = conversation.Mode,
                pageArea = conversation.PageArea,
                surface = conversation.Surface,
                siteId = conversation.SiteId,
                branch = conversation.Branch,
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
                runs = runs.Select(r => new
                {
                    r.Id,
                    r.Task,
                    r.FromSeq,
                    r.ToSeq,
                    r.Outcome,
                    r.Complexity,
                    changes = JsonNode.Parse(r.ChangesJson),
                    validation = r.ValidationJson is null ? null : JsonNode.Parse(r.ValidationJson),
                    metrics = r.MetricsJson is null ? null : JsonNode.Parse(r.MetricsJson),
                    r.StartedAt,
                    r.FinishedAt,
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
                Surface = AiSurfaces.Normalise(body.Surface),
                // A site only means something on the IDE surface. Accepting one for a console
                // conversation would produce a row no rail can find: the console rail does not
                // filter by site and the IDE rail does not look at the console surface.
                SiteId = AiSurfaces.Normalise(body.Surface) == AiSurfaces.Ide ? body.SiteId : null,
                Branch = Clamp(body.Branch, 200, null) is { Length: > 0 } b ? b : null,
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
            // The branch follows the conversation rather than being fixed at creation: an
            // operator can switch branches mid-conversation, and the rail's label should say
            // where the work actually is now.
            if (!string.IsNullOrWhiteSpace(body.Branch)) conversation.Branch = Clamp(body.Branch, 200, conversation.Branch);

            await db.SaveChangesAsync(ct);
            // `Seq` is returned because the caller needs it to say which turns a run produced:
            // it is assigned here, and a browser that guessed would be wrong the moment two
            // tabs appended to one conversation.
            return Results.Ok(new { messageCount = conversation.MessageCount, lastSeq = next });
        })
        .RequirePermission(UsePermission)
        .AuditExempt("One record per turn would bury the log; ai.request already names every "
                     + "model call, and each change the agent makes carries its own action.");

        /*
         * Record a run — its start, and later its end.
         *
         * <p><b>Upsert on a browser-generated id, because a run has two halves and a tab can
         * die between them.</b> Under D1 the agent loop runs in the browser, so there is nobody
         * to write a closing record for a run whose tab was closed. Writing only at the end
         * would mean the stored history contained successes and nothing else — the runs worth
         * reviewing are exactly the ones that did not finish.</p>
         *
         * <p>So the browser PUTs once when the run starts and once when it ends, with the same
         * id. A run left with a null <c>FinishedAt</c> is not a bug in this endpoint; it is the
         * honest record of a run that never finished.</p>
         */
        app.MapPut("/api/admin/ai/conversations/{id:guid}/runs/{runId:guid}", async (
            Guid id, Guid runId, RunRequest body, AiDbContext db, ITenantContext tenant,
            CurrentUser me, DcmsMetrics metrics, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var userId = me.RequireUserId();

            var conversation = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(
                c => c.Id == id && c.TenantId == tenantId && c.OwnerUserId == userId, ct);
            if (conversation is null) return Results.NotFound();

            var changes = body.Changes?.ToJsonString() ?? "[]";
            var validation = body.Validation?.ToJsonString();
            var metricsJson = body.Metrics?.ToJsonString();
            if (changes.Length + (validation?.Length ?? 0) + (metricsJson?.Length ?? 0) > MaxRunBytes)
            {
                return Results.BadRequest(new { error = "This run record is too large to store." });
            }

            var run = await db.Runs.FirstOrDefaultAsync(
                r => r.Id == runId && r.TenantId == tenantId && r.ConversationId == id, ct);

            if (run is null)
            {
                run = new AiRun
                {
                    Id = runId,
                    ConversationId = id,
                    TenantId = tenantId,
                    Task = Clamp(body.Task, MaxRunTask, string.Empty),
                };
                db.Runs.Add(run);
            }

            // Every field is optional on the closing call, so a half-populated finish cannot
            // erase what the opening call recorded.
            if (body.FromSeq is { } from) run.FromSeq = from;
            if (body.ToSeq is { } to) run.ToSeq = to;
            if (!string.IsNullOrWhiteSpace(body.Task)) run.Task = Clamp(body.Task, MaxRunTask, run.Task);
            if (body.Outcome is not null) run.Outcome = StorableOutcome(body.Outcome);
            if (body.Complexity is not null) run.Complexity = Clamp(body.Complexity, 16, null);
            if (body.Changes is not null) run.ChangesJson = changes;
            if (body.Validation is not null) run.ValidationJson = validation;
            if (body.Metrics is not null) run.MetricsJson = metricsJson;
            if (body.Finished is true) run.FinishedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);

            /*
             * The run becomes a metric here, on its closing call, because this is the only place
             * a run's boundaries are known server-side — the loop runs in the browser.
             *
             * Only when it finishes. A run whose tab was closed never reaches this line, which is
             * correct: it is visible in `ai.runs` as a row with no finished_at, and counting it
             * as a completion would be the dashboard reporting work that did not happen.
             */
            if (body.Finished is true)
            {
                metrics.AiRun(
                    tenantId,
                    conversation.Surface,
                    run.Outcome,
                    ReadInt(body.Metrics, "turns"),
                    ReadInt(body.Metrics, "toolCalls"),
                    ReadInt(body.Metrics, "wallMs") / 1000.0);
            }

            return Results.Ok(new { id = run.Id, finished = run.FinishedAt is not null });
        })
        .RequirePermission(UsePermission)
        .AuditExempt("A run record is the agent's own account of what it did; each change it "
                     + "actually made carries its own audit action.");

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

    /// <summary>The outcomes a run may claim. Anything else is recorded as a failure rather
    /// than stored verbatim: an unknown outcome is not something a review screen can read, and
    /// "it did not complete" is the safe reading of one.</summary>
    private static string StorableOutcome(string? outcome) => outcome switch
    {
        "completed" or "failed" or "stopped" => outcome,
        _ => "failed",
    };

    /// <summary>
    /// One integer out of the browser's metrics blob.
    ///
    /// <para>Defensive on purpose: this JSON is shaped by the SPA, so a mismatched deploy or a
    /// provider that reported nothing must cost a dimension on a chart rather than a 500 on the
    /// call that is trying to close a run out.</para>
    /// </summary>
    private static int ReadInt(JsonNode? metrics, string property)
    {
        if (metrics?[property] is not { } value) return 0;
        try
        {
            return value.GetValueKind() == System.Text.Json.JsonValueKind.Number
                ? (int)value.GetValue<double>()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string Clamp(string? value, int max, string? fallback)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return fallback ?? string.Empty;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private sealed record ConversationSummary(
        Guid Id, string Title, string Visibility, string Mode, string? PageArea,
        string Surface, Guid? SiteId, string? Branch,
        int MessageCount, Guid OwnerUserId, bool Mine, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private sealed record CreateConversationRequest(
        string? Title, string? Mode, string? PageArea, string? Surface, Guid? SiteId, string? Branch);

    private sealed record UpdateConversationRequest(
        string? Title, string? Visibility, string? Mode, bool? Archived);

    private sealed record AppendMessagesRequest(
        List<AppendMessage>? Messages, string? Title, string? Mode, string? Branch);

    /// <summary>
    /// A run record, upserted. The browser owns the id so that start and finish are the same
    /// row — see the endpoint for why that matters when a tab dies mid-run.
    /// </summary>
    private sealed record RunRequest(
        string? Task, int? FromSeq, int? ToSeq, string? Outcome, string? Complexity,
        JsonNode? Changes, JsonNode? Validation, JsonNode? Metrics, bool? Finished);

    private sealed record AppendMessage(string Role, JsonArray? Content);
}
