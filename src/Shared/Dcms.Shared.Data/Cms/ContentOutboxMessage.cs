using Dcms.Shared.Audit.Redaction;
namespace Dcms.Shared.Data.Cms;

/// <summary>
/// Transactional outbox row. Written in the same transaction as a content
/// publish/unpublish; a dispatcher relays it to NATS and stamps SentAt
/// (at-least-once delivery; consumers are idempotent).
/// </summary>
// Transport for a change that is audited by the very transaction enqueuing it.
[AuditIgnore]
public sealed class ContentOutboxMessage : TenantEntity
{
    public string Subject { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public int Attempts { get; set; }

    /// <summary>
    /// The audit context of the request that enqueued this row, as propagation headers.
    ///
    /// <para>Without it attribution dies here. The dispatcher polls every two seconds with no
    /// request anywhere near it, so by the time this is published the person who caused it is
    /// long gone — and content publish is exactly the path that has to reach the site builder
    /// still naming them.</para>
    /// </summary>
    public string? ContextJson { get; set; }
}
