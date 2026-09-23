namespace Dcms.Shared.Contracts.Events;

public enum MediaCategory
{
    Image,
    Video,
    Audio,
    File,
}

public sealed record MediaProcessRequested(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid AssetId,
    MediaCategory Category,
    string OriginalObjectKey,
    string ContentType) : ITenantEvent
{
    public int Version => 1;
}

public sealed record MediaProcessed(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid AssetId,
    MediaCategory Category,
    IReadOnlyList<string> VariantKinds) : ITenantEvent
{
    public int Version => 1;
}

public sealed record MediaFailed(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid AssetId,
    string Reason) : ITenantEvent
{
    public int Version => 1;
}
