namespace Dcms.Shared.Data.Ai;

/// <summary>
/// How far a conversation reaches beyond the person who had it.
/// </summary>
public enum AiConversationVisibility
{
    /// <summary>Only the owner (and a holder of <c>ai:chats:read-all</c>) can read it.</summary>
    Private = 0,

    /// <summary>Readable by every member of the workspace. Still writable only by its owner.</summary>
    Workspace = 1,
}

/// <summary>
/// One assistant conversation, owned by the member who started it.
///
/// <para>The assistant used to keep its transcript in React state, which meant a reload lost
/// the reasoning behind whatever it had just changed. Storing it here is what makes a chat
/// resumable, shareable and — for a workspace owner holding <c>ai:chats:read-all</c> —
/// reviewable: these transcripts are the record of an agent writing to tenant content.</para>
///
/// <para><see cref="Mode"/> is the access posture the operator last used, remembered per
/// conversation rather than globally so that resuming a careful conversation does not resume it
/// with an unattended agent. <c>Full auto</c> is deliberately never stored — see
/// <c>modes.ts</c> in the admin SPA for why.</para>
/// </summary>
public sealed class AiConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    /// <summary>The platform user who started it. Only they may write to it or rename it.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Taken from the opening question, and editable afterwards.</summary>
    public string Title { get; set; } = string.Empty;

    public AiConversationVisibility Visibility { get; set; } = AiConversationVisibility.Private;

    /// <summary>The access mode this conversation was last run in: read, careful or agent.</summary>
    public string Mode { get; set; } = "agent";

    /// <summary>The product area the conversation started in ("media", "content"), if any.</summary>
    public string? PageArea { get; set; }

    /// <summary>
    /// Denormalised so the conversation rail can be drawn from one query.
    /// </summary>
    public int MessageCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set instead of deleting, so a shared conversation someone linked to survives.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    public List<AiMessage> Messages { get; set; } = [];
}
