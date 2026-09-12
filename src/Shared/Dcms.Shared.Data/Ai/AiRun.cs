namespace Dcms.Shared.Data.Ai;

/// <summary>
/// One agent run: a single task, however many turns it took.
///
/// <para><b>Why this is not a message.</b> A run is not a turn in the Messages API, and putting
/// it in <c>ai.messages</c> would corrupt the array handed straight back to the model on resume.
/// It is also not derivable from the messages: the outcome, how the task was classified, whether
/// the deterministic build gate passed, and what the run's transaction ended up writing are all
/// facts the loop knows and never says to the model.</para>
///
/// <para><b>What is deliberately NOT stored here: the diff.</b> Every edit the agent made is
/// already in the transcript, in the <c>tool_use</c> block that made it — <c>edit_file</c>
/// carries its anchor and its replacement, <c>replace_lines</c> its range and its text. Copying
/// that into a second column would double the storage for one representation that can drift
/// from the other. What this table adds is the <i>summary</i> the transaction computed across
/// the whole run, which no single message has: which paths ended up changed once the
/// back-and-forth collapsed.</para>
/// </summary>
public sealed class AiRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }

    /// <summary>Denormalised from the conversation: RLS policies are per table, not per graph.</summary>
    public Guid TenantId { get; set; }

    /// <summary>What was asked, verbatim, clamped. The rail reads runs without loading turns.</summary>
    public string Task { get; set; } = string.Empty;

    /// <summary>
    /// The message <c>Seq</c> this run's first turn took, so a run can be located in a
    /// transcript that may contain several. Zero when the run produced no stored turn at all.
    /// </summary>
    public int FromSeq { get; set; }

    /// <summary>The last <c>Seq</c> this run produced. Equal to <see cref="FromSeq"/> for a one-turn run.</summary>
    public int ToSeq { get; set; }

    /// <summary>How it ended: <c>completed</c>, <c>failed</c> or <c>stopped</c>.</summary>
    public string Outcome { get; set; } = "completed";

    /// <summary>How the task was classified: <c>trivial</c>, <c>normal</c> or <c>complex</c>.</summary>
    public string? Complexity { get; set; }

    /// <summary>
    /// The paths the run's transaction ended up changing, as JSON:
    /// <c>[{ "path": "src/App.tsx", "kind": "modified" }]</c>.
    /// </summary>
    public string ChangesJson { get; set; } = "[]";

    /// <summary>
    /// What the deterministic gate said, as JSON: <c>{ "ok": true, "report": "..." }</c>.
    ///
    /// <para>Null means the run was never checked — the preview pane was closed, so there was
    /// nothing to build against. That is a third state and not a failure, and collapsing it into
    /// <c>ok: false</c> would make an unverified run read as a broken one.</para>
    /// </summary>
    public string? ValidationJson { get; set; }

    /// <summary>Token and call totals for the run, as JSON. Null when nothing reported usage.</summary>
    public string? MetricsJson { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedAt { get; set; }

    public AiConversation? Conversation { get; set; }
}
