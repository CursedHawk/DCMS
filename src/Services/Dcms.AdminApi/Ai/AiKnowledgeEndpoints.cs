using System.Text.RegularExpressions;
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
/// The workspace's AI knowledge store (ADR 0034): what agents and people saved — decisions,
/// setups, information, archives of compacted conversations — at folder-like paths.
///
/// <para>Any member reads it, because every member's agents do. Writing and deleting need
/// <see cref="PlatformPermissions.AiKnowledge"/>: an entry is read by other members' agents, and a
/// store anyone could write is a way to put words in all of them.</para>
///
/// <para>Scope: a request naming a site sees workspace-wide entries and that site's; one asking
/// for all sites sees everything; one naming neither sees the workspace's alone.</para>
///
/// <para>An entry linked to a conversation — the archive of a compacted part of it — is as private
/// as that conversation: it summarizes it. Only whoever may read the conversation sees it, and only
/// the conversation's owner may link an entry to it. Once the conversation is gone, its archive is
/// its creator's.</para>
/// </summary>
// rls: request tenant. Every query is the request's own tenant (tenant.TenantId), which the
// interceptor tells the database.
public static partial class AiKnowledgeEndpoints
{
    private const int MaxBodyChars = 64 * 1024;
    private const int MaxListed = 2000;
    private const int MaxHits = 25;

    public static IEndpointRouteBuilder MapAiKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/ai/knowledge", async (
            string? folder, string? siteId, bool? allSites, string? q, bool? recursive,
            AiDbContext db, ITenantContext tenant, CurrentUser me, IPermissionResolver permissions, CancellationToken ct) =>
        {
            if (!tenant.HasTenant) return Results.BadRequest(new { error = "No tenant." });
            if (!TryScope(siteId, out var site)) return Results.BadRequest(new { error = "siteId is not a site id." });
            var tenantId = tenant.TenantId!.Value;
            var root = Normalise(folder ?? "");
            var prefix = root.Length == 0 ? "" : root + "/";

            var scoped = db.Knowledge.AsNoTracking().Where(k => k.TenantId == tenantId);
            scoped = allSites == true ? scoped
                : site is { } s ? scoped.Where(k => k.SiteId == null || k.SiteId == s)
                : scoped.Where(k => k.SiteId == null);
            if (prefix.Length > 0) scoped = scoped.Where(k => k.Path.StartsWith(prefix));

            if (!string.IsNullOrWhiteSpace(q))
            {
                // Inline, not hoisted into a variable: EF translates the call only inside the query.
                var hits = await scoped
                    .Where(k => k.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("simple", q)))
                    .OrderByDescending(k => k.SearchVector.Rank(EF.Functions.WebSearchToTsQuery("simple", q)))
                    .ThenByDescending(k => k.UpdatedAt)
                    .Take(MaxHits)
                    .Select(Row)
                    .ToListAsync(ct);
                var visible = await VisibleAsync(hits, db, permissions, me, tenantId, ct);
                return Results.Ok(new { folder = root, folders = Array.Empty<string>(), entries = visible });
            }

            var all = await VisibleAsync(
                await scoped.OrderBy(k => k.Path).Take(MaxListed).Select(Row).ToListAsync(ct), db, permissions, me, tenantId, ct);
            if (recursive == true) return Results.Ok(new { folder = root, folders = Array.Empty<string>(), entries = all });

            // Like `ls`: what sits directly in the folder, and the folders below it.
            var entries = all.Where(k => !k.path[prefix.Length..].Contains('/')).ToList();
            var folders = all
                .Select(k => k.path[prefix.Length..])
                .Where(rest => rest.Contains('/'))
                .Select(rest => prefix + rest[..rest.IndexOf('/')])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            return Results.Ok(new { folder = root, folders, entries });
        }).RequireAuthorization();

        app.MapGet("/api/admin/ai/knowledge/entry", async (
            string path, string? siteId, AiDbContext db, ITenantContext tenant, CurrentUser me,
            IPermissionResolver permissions, CancellationToken ct) =>
        {
            if (!tenant.HasTenant) return Results.BadRequest(new { error = "No tenant." });
            if (!TryScope(siteId, out var site)) return Results.BadRequest(new { error = "siteId is not a site id." });
            var tenantId = tenant.TenantId!.Value;
            var key = Normalise(path);

            // The site's own entry wins over a workspace-wide one at the same path.
            var entry = await db.Knowledge.AsNoTracking()
                .Where(k => k.TenantId == tenantId && k.Path == key && (k.SiteId == null || k.SiteId == site))
                .OrderBy(k => k.SiteId == null)
                .FirstOrDefaultAsync(ct);
            return entry is null || !await MayReadAsync(entry, db, permissions, me, tenantId, ct)
                ? Results.NotFound(new { error = $"Nothing is saved at {key}. List or search to find the right path." })
                : Results.Ok(Full(entry));
        }).RequireAuthorization();

        app.MapPut("/api/admin/ai/knowledge/entry", async (
            SaveKnowledgeRequest body, AiDbContext db, ITenantContext tenant, CurrentUser me,
            IPermissionResolver permissions, IAuditRecorder audit, CancellationToken ct) =>
        {
            if (!tenant.HasTenant) return Results.BadRequest(new { error = "No tenant." });
            if (!TryScope(body.SiteId, out var site)) return Results.BadRequest(new { error = "siteId is not a site id." });
            var path = Normalise(body.Path ?? "");
            if (!ValidPath().IsMatch(path) || path.Length > 200)
            {
                return Results.BadRequest(new { error = "The path must be lower-case words and hyphens, folders separated by '/', at most 200 characters." });
            }
            var title = body.Title?.Trim() ?? "";
            var description = body.Description?.Trim() ?? "";
            if (title.Length is 0 or > 200) return Results.BadRequest(new { error = "The title must be 1–200 characters." });
            if (description.Length is 0 or > 500) return Results.BadRequest(new { error = "The description must be one line of at most 500 characters." });
            if ((body.Body?.Length ?? 0) > MaxBodyChars) return Results.BadRequest(new { error = "The entry is longer than 64 KB. Split it." });
            var source = body.Source ?? "member";
            if (!AiKnowledgeEntry.Sources.Contains(source)) return Results.BadRequest(new { error = "Unknown source." });

            var tenantId = tenant.TenantId!.Value;
            // Only the owner of a conversation archives it: anyone else could attach a summary of
            // their own making to someone else's conversation, or read one by linking to it.
            if (body.ConversationId is { } linked
                && !await db.Conversations.AnyAsync(c => c.Id == linked && c.TenantId == tenantId && c.OwnerUserId == me.UserId, ct))
            {
                return Results.BadRequest(new { error = "An entry can only be linked to a conversation of your own." });
            }
            var entry = await db.Knowledge.FirstOrDefaultAsync(
                k => k.TenantId == tenantId && k.SiteId == site && k.Path == path, ct);
            // Nobody overwrites an archive they may not read.
            if (entry is not null && !await MayReadAsync(entry, db, permissions, me, tenantId, ct))
            {
                return Results.Conflict(new { error = "path-taken", message = "That path is taken. Choose another." });
            }
            if (entry is not null && body.ExpectedUpdatedAt is { } expected
                && Math.Abs((entry.UpdatedAt - expected).TotalMilliseconds) >= 1)
            {
                return Results.Conflict(new { error = "knowledge-changed", message = "Someone saved this entry since you opened it. Reload it and try again." });
            }

            var now = DateTimeOffset.UtcNow;
            if (entry is null)
            {
                entry = new AiKnowledgeEntry { Id = Guid.NewGuid(), TenantId = tenantId, SiteId = site, Path = path, CreatedBy = me.UserId, CreatedAt = now };
                db.Knowledge.Add(entry);
            }
            entry.Title = title;
            entry.Description = description;
            entry.Body = body.Body ?? "";
            entry.Source = source;
            entry.ConversationId = body.ConversationId;
            entry.FromSeq = body.FromSeq;
            entry.ToSeq = body.ToSeq;
            entry.UpdatedBy = me.UserId;
            entry.UpdatedAt = now;

            audit.Declared?.For("ai_knowledge", entry.Id, path).With("source", source).With("site", site);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new KnowledgeSummary(entry.Id, entry.SiteId, entry.Path, entry.Title, entry.Description, entry.Source, entry.UpdatedAt));
        })
            .RequirePermission(PlatformPermissions.AiKnowledge)
            .WithAudit(AuditActions.AiKnowledgeSaved, "ai_knowledge");

        app.MapDelete("/api/admin/ai/knowledge/{id:guid}", async (
            Guid id, AiDbContext db, ITenantContext tenant, CurrentUser me, IPermissionResolver permissions,
            IAuditRecorder audit, CancellationToken ct) =>
        {
            if (!tenant.HasTenant) return Results.BadRequest(new { error = "No tenant." });
            var tenantId = tenant.TenantId!.Value;
            var entry = await db.Knowledge.FirstOrDefaultAsync(k => k.TenantId == tenantId && k.Id == id, ct);
            // An archive the caller may not read is one they may not delete either.
            if (entry is null || !await MayReadAsync(entry, db, permissions, me, tenantId, ct)) return Results.NotFound();
            audit.Declared?.For("ai_knowledge", entry.Id, entry.Path);
            db.Knowledge.Remove(entry);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
            .RequirePermission(PlatformPermissions.AiKnowledge)
            .WithAudit(AuditActions.AiKnowledgeDeleted, "ai_knowledge");

        return app;
    }

    private static bool TryScope(string? siteId, out Guid? site)
    {
        site = null;
        if (string.IsNullOrWhiteSpace(siteId)) return true;
        if (!Guid.TryParse(siteId, out var parsed)) return false;
        site = parsed;
        return true;
    }

    /// <summary>Lower-case, trimmed of slashes and spaces; the client normalises the same way.</summary>
    private static string Normalise(string path) => path.Trim().Trim('/').ToLowerInvariant();

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:/[a-z0-9]+(?:-[a-z0-9]+)*)*$")]
    private static partial Regex ValidPath();

    private static readonly System.Linq.Expressions.Expression<Func<AiKnowledgeEntry, KnowledgeRow>> Row =
        k => new KnowledgeRow(new KnowledgeSummary(k.Id, k.SiteId, k.Path, k.Title, k.Description, k.Source, k.UpdatedAt), k.ConversationId, k.CreatedBy);

    /// <summary>A listed entry with what deciding its visibility needs.</summary>
    private sealed record KnowledgeRow(KnowledgeSummary Summary, Guid? ConversationId, Guid? CreatedBy);

    /// <summary>The rows the caller may see: an archive only with its conversation (see the class remarks).</summary>
    private static async Task<List<KnowledgeSummary>> VisibleAsync(
        IReadOnlyList<KnowledgeRow> rows, AiDbContext db, IPermissionResolver permissions, CurrentUser me,
        Guid tenantId, CancellationToken ct)
    {
        var linked = rows.Select(r => r.ConversationId).OfType<Guid>().Distinct().ToList();
        if (linked.Count == 0) return rows.Select(r => r.Summary).ToList();

        var userId = me.RequireUserId();
        var conversations = await db.Conversations.AsNoTracking()
            .Where(c => c.TenantId == tenantId && linked.Contains(c.Id))
            .ToListAsync(ct);
        var readable = new HashSet<Guid>();
        foreach (var conversation in conversations)
        {
            if (await AiConversationEndpoints.CanReadAsync(conversation, permissions, me, tenantId, userId, ct))
            {
                readable.Add(conversation.Id);
            }
        }
        var existing = conversations.Select(c => c.Id).ToHashSet();
        return rows
            .Where(r => r.ConversationId is not { } id
                        || readable.Contains(id)
                        || (!existing.Contains(id) && r.CreatedBy == userId))
            .Select(r => r.Summary)
            .ToList();
    }

    private static async Task<bool> MayReadAsync(
        AiKnowledgeEntry entry, AiDbContext db, IPermissionResolver permissions, CurrentUser me, Guid tenantId, CancellationToken ct) =>
        (await VisibleAsync(
            [new KnowledgeRow(new KnowledgeSummary(entry.Id, entry.SiteId, entry.Path, entry.Title, entry.Description, entry.Source, entry.UpdatedAt),
                entry.ConversationId, entry.CreatedBy)],
            db, permissions, me, tenantId, ct)).Count == 1;

    private static object Full(AiKnowledgeEntry k) => new
    {
        id = k.Id, siteId = k.SiteId, path = k.Path, title = k.Title, description = k.Description, body = k.Body,
        source = k.Source, conversationId = k.ConversationId, fromSeq = k.FromSeq, toSeq = k.ToSeq,
        createdAt = k.CreatedAt, updatedAt = k.UpdatedAt,
    };

    /// <summary>What listings carry: everything but the body.</summary>
    public sealed record KnowledgeSummary(
        Guid id, Guid? siteId, string path, string title, string description, string source, DateTimeOffset updatedAt);

    public sealed record SaveKnowledgeRequest(
        string? SiteId, string? Path, string? Title, string? Description, string? Body, string? Source,
        Guid? ConversationId, int? FromSeq, int? ToSeq, DateTimeOffset? ExpectedUpdatedAt);
}
