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
    string ContentType) : IDcmsEvent
{
    public int Version => 1;
}

public sealed record MediaProcessed(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid AssetId,
    MediaCategory Category,
    IReadOnlyList<string> VariantKinds) : IDcmsEvent
{
    public int Version => 1;
}

public sealed record MediaFailed(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid AssetId,
    string Reason) : IDcmsEvent
{
    public int Version => 1;
}
