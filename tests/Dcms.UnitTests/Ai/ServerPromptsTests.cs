using System.Text;
using Dcms.Shared.Data.Ai;

namespace Dcms.UnitTests.Ai;

/// <summary>
/// The site chatbot's prompt moved into a catalogue the platform console can override. With no
/// overrides it must be exactly the prompt the bot always sent; this is that builder, kept here as
/// the reference.
/// </summary>
public class ServerPromptsTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData("Answer only about opening hours.", null, false)]
    [InlineData(null, "## Opening hours\nMon–Fri 9–17\n\n", false)]
    [InlineData("Be formal.", "## Prices\nFrom 10 EUR\n\n", true)]
    [InlineData(null, null, true)]
    public void Without_overrides_the_prompt_is_the_one_the_bot_always_sent(string? instructions, string? content, bool tools)
    {
        var expected = Legacy("Ava", instructions, content)
            + (tools ? "\nUse the tools when they can answer the visitor's question from this website's data." : "");

        ServerPrompts.ChatbotPrompt("Ava", instructions, content, tools).Should().Be(expected);
    }

    [Fact]
    public void An_override_replaces_its_part_and_fills_its_placeholders_once()
    {
        var overrides = new Dictionary<string, string>
        {
            [ServerPrompts.Chatbot] = "You are {{botName}} of the HR desk.",
            [ServerPrompts.ChatbotUngrounded] = "   ", // blank: the shipped text stays
        };

        // A bot named after a placeholder is not filled a second time.
        ServerPrompts.ChatbotPrompt("{{content}}", null, null, withTools: false, overrides)
            .Should().Be("You are {{content}} of the HR desk.\n"
                         + "If you don't know the answer, say so plainly and offer to connect a team member.\n");
    }

    [Fact]
    public void Unreadable_overrides_are_none()
    {
        ServerPrompts.ParseOverrides("{not json").Should().BeEmpty();
        ServerPrompts.ParseOverrides(null).Should().BeEmpty();
    }

    /// <summary>ChatBotResponder.SystemPrompt as it was before the catalogue (2026-11-13).</summary>
    private static string Legacy(string botName, string? instructions, string? context)
    {
        var system = new StringBuilder();
        system.Append("You are ").Append(botName)
            .Append(", a helpful assistant embedded on a website, chatting with a visitor.").Append('\n');
        system.Append("Be concise, friendly and accurate. Answer in the visitor's language.").Append('\n');
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            system.Append('\n').Append(instructions).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(context))
        {
            system.Append('\n')
                .Append("Use the following content from this website to answer. Prefer it over prior knowledge. "
                        + "If the answer isn't in it and you're unsure, say so plainly and offer to connect a team member.").Append('\n')
                .Append('\n')
                .Append("=== SITE CONTENT ===").Append('\n')
                .Append(context).Append('\n')
                .Append("=== END SITE CONTENT ===").Append('\n');
        }
        else
        {
            system.Append("If you don't know the answer, say so plainly and offer to connect a team member.").Append('\n');
        }
        return system.ToString();
    }
}
