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
    /// Which surface held the conversation: <c>console</c> (the assistant dock) or <c>ide</c>
    /// (the Mode B site builder's agent).
    ///
    /// <para><b>One table, filtered — not two tables.</b> The two surfaces run the same agent
    /// over the same wire format, so their turns are byte-for-byte the same kind of row. A
    /// second table would have duplicated the whole review story that already hangs off this
    /// one: the <c>ai:chats:read-all</c> permission, the workspace-visibility rule, the RLS
    /// registration, the retention sweep. An agent that can publish a site needs reviewing for
    /// exactly the reason an agent that can publish content does.</para>
    ///
    /// <para>What the column must actually earn is the filtering: every rail query names a
    /// surface. Without that the IDE's runs — which are many and short-lived — would bury the
    /// console's conversations in a shared list, which is the one real argument for splitting
    /// the table and is answered by an index instead.</para>
    /// </summary>
    public string Surface { get; set; } = AiSurfaces.Console;

    /// <summary>The site an IDE conversation belongs to. Null for the console assistant.</summary>
    public Guid? SiteId { get; set; }

    /// <summary>
    /// The branch this conversation was last run against.
    ///
    /// <para><b>Last, not first.</b> A branch is a property of when a turn happened rather than
    /// of the conversation: somebody can start on <c>main</c>, switch to a feature branch and
    /// keep talking. Storing the latest is what makes the rail's label true right now, which is
    /// what it is read for; a turn-by-turn history of the branch would live on the run, and
    /// nothing has asked for it.</para>
    /// </summary>
    public string? Branch { get; set; }

    /// <summary>
    /// Denormalised so the conversation rail can be drawn from one query.
    /// </summary>
    public int MessageCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set instead of deleting, so a shared conversation someone linked to survives.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    public List<AiMessage> Messages { get; set; } = [];

    public List<AiRun> Runs { get; set; } = [];
}

/// <summary>The surfaces a conversation can belong to. Stored as a plain string, not an enum,
/// because the browser sends it and a third surface should cost a constant rather than a
/// migration.</summary>
public static class AiSurfaces
{
    public const string Console = "console";
    public const string Ide = "ide";

    public static bool IsKnown(string? value) => value is Console or Ide;

    /// <summary>Anything unrecognised is the console, which is the surface that predates the
    /// column and therefore what every existing row is.</summary>
    public static string Normalise(string? value) => IsKnown(value) ? value! : Console;
}
