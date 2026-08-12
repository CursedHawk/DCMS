using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dcms.Shared.Data.Chat;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Search;
using Dcms.Shared.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Chat;

/// <summary>
/// Generates the AI assistant's replies for the visitor chat. Triggered fire-and-forget
/// by <see cref="ChatHub"/> after a visitor message: it reads the tenant's AI Chatbot
/// (<c>live-chat</c>) instance config, optionally grounds the answer in the tenant's
/// published content (RAG over the search index), calls ai-gateway for a completion,
/// persists the reply as a <see cref="ChatSender.Bot"/> message, and fans it out over
/// SignalR into the same per-conversation / per-agent groups the hub uses.
///
/// Runs outside any HTTP request, so every DB query ignores the ambient tenant query
/// filter and scopes explicitly by tenant id (there is no <c>ITenantContext</c> here).
/// A human takeover — any <see cref="ChatSender.Agent"/> message in the conversation —
/// silences the bot when <c>humanHandoff</c> is on.
/// </summary>
public sealed class ChatBotResponder(
    IServiceProvider services,
    IHubContext<ChatHub> hub,
    IHttpClientFactory httpClientFactory,
    ILogger<ChatBotResponder> logger)
{
    private const string PluginId = "live-chat";
    private const string AiScope = "dcms.ai";
    private const int HistoryLimit = 12;
    private const int MaxContextDocs = 5;
    private const int MaxDocChars = 700;

    /// <summary>Fire-and-forget entry point; never throws into the caller (the hub).</summary>
    public void Trigger(Guid tenantId, Guid conversationId, string visitorMessage)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RespondAsync(tenantId, conversationId, visitorMessage, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AI chat reply failed for conversation {ConversationId}", conversationId);
            }
        });
    }

    private async Task RespondAsync(Guid tenantId, Guid conversationId, string visitorMessage, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var cms = sp.GetRequiredService<CmsDbContext>();
        var chat = sp.GetRequiredService<ChatDbContext>();

        var instance = await cms.PluginInstances.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.PluginId == PluginId && p.Enabled, ct);
        if (instance is null)
        {
            return; // Chatbot not installed/enabled for this tenant.
        }
        var config = BotConfig.Parse(instance.ConfigJson);

        var conversation = await chat.Conversations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.TenantId == tenantId, ct);
        if (conversation is null || conversation.Status == ChatConversationStatus.Closed)
        {
            return;
        }

        // A human agent reply takes the conversation over — the bot goes quiet.
        if (config.HumanHandoff)
        {
            var humanReplied = await chat.Messages.IgnoreQueryFilters()
                .AnyAsync(m => m.TenantId == tenantId && m.ConversationId == conversationId
                               && m.Sender == ChatSender.Agent, ct);
            if (humanReplied)
            {
                return;
            }
        }

        var history = await chat.Messages.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.ConversationId == conversationId)
            .OrderByDescending(m => m.SentAt)
            .Take(HistoryLimit)
            .Select(m => new { m.Sender, m.Body })
            .ToListAsync(ct);
        history.Reverse();

        var context = config.Grounding
            ? await RetrieveContextAsync(sp, tenantId, visitorMessage, ct)
            : null;

        var tokens = sp.GetRequiredService<IServiceTokenProvider>();
        var reply = await CompleteAsync(tokens, tenantId, config, context, history.Select(h => (h.Sender, h.Body)), ct);
        if (string.IsNullOrWhiteSpace(reply))
        {
            return;
        }
        reply = reply.Trim();
        if (reply.Length > 8000)
        {
            reply = reply[..8000];
        }

        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conversationId,
            Sender = ChatSender.Bot,
            SenderId = null,
            Body = reply,
            SentAt = now,
            IsSandbox = conversation.IsSandbox,
        };
        chat.Messages.Add(message);
        conversation.LastMessageAt = now;
        await chat.SaveChangesAsync(ct);

        var dto = new
        {
            id = message.Id,
            conversationId,
            sender = ChatSender.Bot.ToString(),
            body = reply,
            sentAt = now,
        };
        await hub.Clients.Group(ChatHub.ConversationGroup(conversationId)).SendAsync("ReceiveMessage", dto, ct);
        await hub.Clients.Group(ChatHub.AgentGroup(tenantId)).SendAsync("ConversationActivity", new
        {
            id = conversationId,
            visitorName = conversation.VisitorName,
            lastMessageAt = now,
            preview = reply.Length > 120 ? reply[..120] : reply,
        }, ct);
    }

    /// <summary>Top matching published-content snippets for the visitor's message.</summary>
    private static async Task<string?> RetrieveContextAsync(
        IServiceProvider sp, Guid tenantId, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }
        var search = sp.GetRequiredService<SearchDbContext>();
        // EF.Functions.* must stay inside the LINQ expression (it throws if invoked
        // directly), so the tsquery is rebuilt inline rather than hoisted to a local.
        var docs = await search.Documents.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId
                        && d.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("simple", query)))
            .OrderByDescending(d => d.SearchVector.Rank(EF.Functions.WebSearchToTsQuery("simple", query)))
            .Take(MaxContextDocs)
            .Select(d => new { d.Title, d.Body, d.Url })
            .ToListAsync(ct);
        if (docs.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var d in docs)
        {
            var body = d.Body.Length > MaxDocChars ? d.Body[..MaxDocChars] + "…" : d.Body;
            sb.Append("## ").AppendLine(d.Title);
            if (!string.IsNullOrWhiteSpace(d.Url))
            {
                sb.Append("URL: ").AppendLine(d.Url);
            }
            sb.AppendLine(body).AppendLine();
        }
        return sb.ToString();
    }

    private async Task<string?> CompleteAsync(
        IServiceTokenProvider tokens, Guid tenantId, BotConfig config, string? context,
        IEnumerable<(ChatSender Sender, string Body)> history, CancellationToken ct)
    {
        var system = new StringBuilder();
        system.Append("You are ").Append(config.BotName)
            .AppendLine(", a helpful assistant embedded on a website, chatting with a visitor.");
        system.AppendLine("Be concise, friendly and accurate. Answer in the visitor's language.");
        if (!string.IsNullOrWhiteSpace(config.Instructions))
        {
            system.AppendLine().AppendLine(config.Instructions);
        }
        if (!string.IsNullOrWhiteSpace(context))
        {
            system.AppendLine()
                .AppendLine("Use the following content from this website to answer. Prefer it over prior knowledge. "
                            + "If the answer isn't in it and you're unsure, say so plainly and offer to connect a team member.")
                .AppendLine()
                .AppendLine("=== SITE CONTENT ===")
                .AppendLine(context)
                .AppendLine("=== END SITE CONTENT ===");
        }
        else
        {
            system.AppendLine("If you don't know the answer, say so plainly and offer to connect a team member.");
        }

        var transcript = new StringBuilder();
        foreach (var (sender, body) in history)
        {
            transcript.Append(sender == ChatSender.Visitor ? "Visitor: " : "Assistant: ").AppendLine(body);
        }
        transcript.Append("Assistant:");

        var envelope = new
        {
            tenantId,
            prompt = transcript.ToString(),
            system = system.ToString(),
            maxTokens = 800,
        };

        var token = await tokens.GetTokenAsync(AiScope, ct);
        var client = httpClientFactory.CreateClient("ai-gateway");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat")
        {
            Content = JsonContent.Create(envelope),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("ai-gateway /v1/chat returned {Status} for tenant {TenantId}",
                (int)response.StatusCode, tenantId);
            return null;
        }
        var payload = await response.Content.ReadFromJsonAsync<ChatCompletion>(ct);
        return payload?.Text;
    }

    private sealed record ChatCompletion(string Provider, string Model, string Text);

    private sealed record BotConfig(
        string BotName, string? Instructions, bool Grounding, bool HumanHandoff)
    {
        public static BotConfig Parse(string? json)
        {
            var botName = "Assistant";
            string? instructions = null;
            var grounding = true;
            var humanHandoff = true;

            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        if (root.TryGetProperty("botName", out var n) && n.ValueKind == JsonValueKind.String
                            && n.GetString() is { Length: > 0 } name)
                        {
                            botName = name;
                        }
                        if (root.TryGetProperty("instructions", out var i) && i.ValueKind == JsonValueKind.String)
                        {
                            instructions = i.GetString();
                        }
                        if (root.TryGetProperty("grounding", out var g)
                            && g.ValueKind is JsonValueKind.False or JsonValueKind.True)
                        {
                            grounding = g.GetBoolean();
                        }
                        if (root.TryGetProperty("humanHandoff", out var h)
                            && h.ValueKind is JsonValueKind.False or JsonValueKind.True)
                        {
                            humanHandoff = h.GetBoolean();
                        }
                    }
                }
                catch (JsonException)
                {
                    // Hand-editable config; fall back to defaults on malformed JSON.
                }
            }

            return new BotConfig(botName, instructions, grounding, humanHandoff);
        }
    }
}
