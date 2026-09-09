namespace Dcms.Shared.Data.Ai;

/// <summary>
/// One turn in an <see cref="AiConversation"/>, stored in the wire format the browser loop
/// already speaks.
///
/// <para><see cref="Content"/> holds the Anthropic content blocks verbatim as JSON — text,
/// <c>tool_use</c> and <c>tool_result</c> alike. Resuming a conversation is then the array
/// itself handed back to the model with no translation, and the transcript's work cards read
/// the same blocks. One representation; nothing to drift. (Which provider actually served the
/// turn is irrelevant here: ai-gateway translates at the edge, so the browser and this table
/// only ever see one dialect.)</para>
///
/// <para>Tool results are stored whole, which means draft content and analytics figures land in
/// this table. That is what makes a resumed conversation's detail cards work at all; the rows
/// are tenant-scoped and RLS-covered, and sharing a conversation shares its tool results with
/// it.</para>
/// </summary>
public sealed class AiMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }

    /// <summary>Denormalised from the conversation: RLS policies are per table, not per graph.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Position in the conversation, unique within it. Gapless is not required.</summary>
    public int Seq { get; set; }

    /// <summary>"user" or "assistant" — the Messages API roles, not a UI distinction.</summary>
    public string Role { get; set; } = "user";

    /// <summary>The content blocks as JSON. Always an array, even for a one-line question.</summary>
    public string Content { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AiConversation? Conversation { get; set; }
}
