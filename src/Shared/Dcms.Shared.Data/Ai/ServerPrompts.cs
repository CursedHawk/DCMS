using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dcms.Shared.Data.Ai;

/// <summary>One server-side system prompt, as the platform console lists it.</summary>
public sealed record ServerPromptDefinition(
    string Key, string Title, string UsedFor, IReadOnlyList<string> Placeholders, string Text);

/// <summary>
/// The system prompts built on the server: the site chatbot's. The browser agents' prompts are
/// catalogued in <c>packages/core/src/aiPrompts.ts</c>; these live here because the services that
/// build them are .NET and their images copy only <c>src/</c>.
///
/// <para>The platform console shows them beside the browser ones and may override any of them
/// per tenant (<see cref="TenantAiSettings.PromptOverridesJson"/>); ai-gateway hands the chatbot
/// its tenant's overrides. Placeholders in double braces are filled in when the prompt is built,
/// and a value is never scanned for further placeholders.</para>
/// </summary>
public static partial class ServerPrompts
{
    public const string Chatbot = "chatbot";
    public const string ChatbotGrounded = "chatbot.grounded";
    public const string ChatbotUngrounded = "chatbot.ungrounded";
    public const string ChatbotTools = "chatbot.tools";

    public static readonly IReadOnlyList<ServerPromptDefinition> All =
    [
        new(Chatbot, "Site chatbot", "Every reply of the website chatbot. The instance's own instructions follow it.",
            ["botName"],
            "You are {{botName}}, a helpful assistant embedded on a website, chatting with a visitor.\n"
            + "Be concise, friendly and accurate. Answer in the visitor's language."),
        new(ChatbotGrounded, "Site chatbot — with site content",
            "Appended when the chatbot found published content matching the visitor's question.",
            ["content"],
            "Use the following content from this website to answer. Prefer it over prior knowledge. "
            + "If the answer isn't in it and you're unsure, say so plainly and offer to connect a team member.\n"
            + "\n"
            + "=== SITE CONTENT ===\n"
            + "{{content}}\n"
            + "=== END SITE CONTENT ==="),
        new(ChatbotUngrounded, "Site chatbot — without site content",
            "Appended when no site content matched, or grounding is off.",
            [],
            "If you don't know the answer, say so plainly and offer to connect a team member."),
        new(ChatbotTools, "Site chatbot — tools", "Appended when the tenant opted plugin tools in for the chatbot.",
            [],
            "Use the tools when they can answer the visitor's question from this website's data."),
    ];

    public static IReadOnlySet<string> Keys { get; } = All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>A prompt's text for a tenant: its override when it has a non-blank one.</summary>
    public static string Text(string key, IReadOnlyDictionary<string, string>? overrides) =>
        overrides is not null && overrides.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : All.First(p => p.Key == key).Text;

    /// <summary>Fill <c>{{name}}</c> placeholders in one pass; an unknown one becomes nothing.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

    /// <summary>
    /// The chatbot's system prompt. With no overrides this is byte-for-byte the prompt the bot
    /// built before it was configurable.
    /// </summary>
    public static string ChatbotPrompt(
        string botName, string? instructions, string? content, bool withTools,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var system = new StringBuilder();
        system.Append(Fill(Text(Chatbot, overrides), new Dictionary<string, string> { ["botName"] = botName })).Append('\n');
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            system.Append('\n').Append(instructions).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(content))
        {
            system.Append('\n')
                .Append(Fill(Text(ChatbotGrounded, overrides), new Dictionary<string, string> { ["content"] = content }))
                .Append('\n');
        }
        else
        {
            system.Append(Text(ChatbotUngrounded, overrides)).Append('\n');
        }
        if (withTools)
        {
            system.Append('\n').Append(Text(ChatbotTools, overrides));
        }
        return system.ToString();
    }

    /// <summary>The overrides stored for a tenant, or none.</summary>
    public static Dictionary<string, string> ParseOverrides(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Placeholder();
}
