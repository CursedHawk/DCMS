namespace Dcms.Shared.Data.Cms;

/// <summary>
/// Transactional outbox row. Written in the same transaction as a content
/// publish/unpublish; a dispatcher relays it to NATS and stamps SentAt
/// (at-least-once delivery; consumers are idempotent).
/// </summary>
public sealed class ContentOutboxMessage : TenantEntity
{
    public string Subject { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public int Attempts { get; set; }
}
