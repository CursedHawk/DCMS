namespace Dcms.Shared.Data.Cms;

public enum ScheduledPublishStatus
{
    Pending,
    Done,
    Failed,
}

/// <summary>
/// A future publish of a specific content version. A polling worker claims due
/// rows with FOR UPDATE SKIP LOCKED, so multiple admin-api replicas can run the
/// scheduler without double-publishing.
/// </summary>
public sealed class ScheduledPublish : TenantEntity
{
    public Guid ItemId { get; set; }
    public Guid VersionId { get; set; }
    public DateTimeOffset PublishAt { get; set; }
    public ScheduledPublishStatus Status { get; set; } = ScheduledPublishStatus.Pending;
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
}
