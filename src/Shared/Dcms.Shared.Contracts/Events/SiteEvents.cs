namespace Dcms.Shared.Contracts.Events;

public sealed record SitePublishRequested(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid SiteId,
    Guid BuildId,
    string RenderMode) : IDcmsEvent
{
    public int Version => 1;
}

public sealed record SitePublished(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid SiteId,
    Guid BuildId,
    string ArtifactPrefix) : IDcmsEvent
{
    public int Version => 1;
}

public sealed record SiteBuildFailed(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid SiteId,
    Guid BuildId,
    string Reason) : IDcmsEvent
{
    public int Version => 1;
}
