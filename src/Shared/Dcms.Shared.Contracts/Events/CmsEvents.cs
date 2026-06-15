namespace Dcms.Shared.Contracts.Events;

public sealed record ContentPublished(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid PluginInstanceId,
    Guid ContentItemId,
    string ContentType,
    string Slug) : IDcmsEvent
{
    public int Version => 1;
}

public sealed record ContentUnpublished(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid PluginInstanceId,
    Guid ContentItemId,
    string ContentType,
    string Slug) : IDcmsEvent
{
    public int Version => 1;
}
