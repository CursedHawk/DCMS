using System.Text;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
using Dcms.Shared.Data.Chat;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Plugins.LiveChat;

/// <summary>
/// Chat history on the plugin's admin page: every conversation with its transcript, closable
/// and deletable. Answering lives in the chat console (<c>/chat</c>).
///
/// <para>Set-based statements here are audited by the data-set endpoints, not per statement: they
/// record every change as <c>plugin.data.*</c> through <c>IAuditRecorder</c>, naming the data
/// set, the action, the rows and the affected count.</para>
/// </summary>
public sealed class ConversationsDataSet(ChatDbContext db) : IPluginDataSet
{
    private const string Close = "close";
    private const int MaxTranscript = 500;

    public Task<DataSetSchema> DescribeAsync(CancellationToken ct) => Task.FromResult(new DataSetSchema(
        Columns:
        [
            new DataColumn("visitorName", "Visitor", Sortable: true, Primary: true),
            new DataColumn("status", "Status", DataColumnKinds.Badge),
            new DataColumn("messages", "Messages", DataColumnKinds.Number),
            new DataColumn("lastMessageAt", "Last message", DataColumnKinds.DateTime, Sortable: true),
            new DataColumn("createdAt", "Started", DataColumnKinds.DateTime, Sortable: true),
        ],
        Filters:
        [
            new DataFilter("status", "Status", [new DataFilterOption("open", "Open"), new DataFilterOption("closed", "Closed")]),
        ],
        Actions: [new DataAction(Close, "Close conversation", OpRisk.Safe)],
        Searchable: true,
        CanDelete: true,
        DefaultSort: "lastMessageAt",
        DefaultDescending: true));

    public async Task<DataPage> ListAsync(DataQuery query, CancellationToken ct)
    {
        var rows = db.Conversations.AsNoTracking();
        if (query.Search is { } search)
        {
            rows = rows.Where(c => EF.Functions.ILike(c.VisitorName, $"%{search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%"));
        }
        rows = query.Filter("status") switch
        {
            "open" => rows.Where(c => c.Status == ChatConversationStatus.Open),
            "closed" => rows.Where(c => c.Status == ChatConversationStatus.Closed),
            _ => rows,
        };
        rows = (query.Sort, query.Descending) switch
        {
            ("visitorName", false) => rows.OrderBy(c => c.VisitorName),
            ("visitorName", true) => rows.OrderByDescending(c => c.VisitorName),
            ("createdAt", false) => rows.OrderBy(c => c.CreatedAt),
            ("createdAt", true) => rows.OrderByDescending(c => c.CreatedAt),
            (_, false) => rows.OrderBy(c => c.LastMessageAt),
            _ => rows.OrderByDescending(c => c.LastMessageAt),
        };
        var total = await rows.LongCountAsync(ct);
        var page = await rows.Skip(query.Skip).Take(query.PageSize)
            .Select(c => new { Conversation = c, Messages = db.Messages.Count(m => m.ConversationId == c.Id) })
            .ToListAsync(ct);
        return new DataPage(page.Select(p => ToRow(p.Conversation, p.Messages)).ToList(), total);
    }

    public async Task<DataRow?> GetAsync(string key, CancellationToken ct)
    {
        if (!Guid.TryParse(key, out var id) || await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) is not { } conversation)
        {
            return null;
        }
        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == id)
            .OrderBy(m => m.SentAt)
            .Take(MaxTranscript)
            .ToListAsync(ct);
        var transcript = new StringBuilder();
        foreach (var m in messages)
        {
            var who = m.Sender switch
            {
                ChatSender.Agent => "Agent",
                ChatSender.Bot => "Assistant",
                _ => conversation.VisitorName,
            };
            transcript.Append($"[{m.SentAt:yyyy-MM-dd HH:mm}] {who}: {m.Body}\n");
        }
        var row = ToRow(conversation, messages.Count);
        row.Values["transcript"] = transcript.ToString().TrimEnd();
        return row;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct)
    {
        if (!Guid.TryParse(key, out var id) || !await db.Conversations.AnyAsync(c => c.Id == id, ct))
        {
            return false;
        }
        await db.Messages.Where(m => m.ConversationId == id).ExecuteDeleteAsync(ct);
        await db.Conversations.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        return true;
    }

    public async Task<DataActionResult> RunActionAsync(string action, IReadOnlyList<string> keys, JsonObject? input, CancellationToken ct)
    {
        if (action != Close)
        {
            throw new NotSupportedException();
        }
        var ids = keys.Select(k => Guid.TryParse(k, out var g) ? g : Guid.Empty).ToList();
        return new DataActionResult(await db.Conversations
            .Where(c => ids.Contains(c.Id) && c.Status == ChatConversationStatus.Open)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ChatConversationStatus.Closed), ct));
    }

    private static DataRow ToRow(ChatConversation c, int messages) => new(c.Id.ToString(), new JsonObject
    {
        ["visitorName"] = c.VisitorName,
        ["status"] = c.Status == ChatConversationStatus.Open ? "open" : "closed",
        ["messages"] = messages,
        ["lastMessageAt"] = c.LastMessageAt,
        ["createdAt"] = c.CreatedAt,
        ["visitorId"] = c.VisitorId?.ToString(),
    }, c.VisitorName);
}
