namespace Dcms.Shared.Data.Social;

/// <summary>
/// Where the sync for one plugin instance × content type got to. Split per content
/// type rather than per instance because Instagram returns feed posts and reels from
/// one endpoint under separate caps — they fill, fail and back off independently.
/// </summary>
public sealed class MetaSyncState : TenantEntity
{
    public Guid PluginInstanceId { get; set; }
    public Guid ConnectionId { get; set; }

    /// <summary>e.g. <c>instagram-post</c>, <c>instagram-reel</c>, <c>facebook-post</c>.</summary>
    public string ContentType { get; set; } = string.Empty;

    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>Meta's paging cursor from the last completed pass, if the cap was not reached.</summary>
    public string? LastCursor { get; set; }

    public int ConsecutiveFailures { get; set; }

    /// <summary>Backoff gate. The worker skips this row until now passes it.</summary>
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    public string? LastError { get; set; }
}
