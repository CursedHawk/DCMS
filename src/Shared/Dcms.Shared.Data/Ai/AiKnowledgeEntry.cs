using NpgsqlTypes;

namespace Dcms.Shared.Data.Ai;

/// <summary>
/// One entry of a workspace's AI knowledge store (ADR 0034): a decision, a setup, a fact about the
/// workspace, or the archive of a compacted conversation, at a folder-like path.
///
/// <para>Agents save entries as they work and read them only when they ask; people curate them in
/// the console. <see cref="SiteId"/> null is workspace-wide; otherwise the entry belongs to one
/// site (or Functions repository), and only that site's agent sees it beside the workspace's.</para>
/// </summary>
public sealed class AiKnowledgeEntry
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The site the entry belongs to; null for the whole workspace.</summary>
    public Guid? SiteId { get; set; }

    /// <summary>Lower-case <c>[a-z0-9-]</c> segments joined by <c>/</c>; unique per tenant and site.</summary>
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>One line: what listings and searches show.</summary>
    public string Description { get; set; } = "";

    /// <summary>Markdown.</summary>
    public string Body { get; set; } = "";

    /// <summary><c>agent</c>, <c>member</c> or <c>compaction</c>.</summary>
    public string Source { get; set; } = "member";

    /// <summary>For a compaction archive: the conversation, and the seq range it summarized.</summary>
    public Guid? ConversationId { get; set; }
    public int? FromSeq { get; set; }
    public int? ToSeq { get; set; }

    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Generated from title, description and body.</summary>
    public NpgsqlTsVector SearchVector { get; set; } = null!;

    public static readonly IReadOnlySet<string> Sources = new HashSet<string>(StringComparer.Ordinal) { "agent", "member", "compaction" };
}
