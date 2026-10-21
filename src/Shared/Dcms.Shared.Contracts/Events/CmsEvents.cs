namespace Dcms.Shared.Contracts.Events;

/// <param name="Quiet">
/// Set by a feed sync for changes that are not news: refreshing an item already live, dropping
/// one the feed no longer has, a first import. Caches and search still act on it; nobody is notified.
/// </param>
public sealed record ContentPublished(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid PluginInstanceId,
    Guid ContentItemId,
    string ContentType,
    string Slug,
    bool Quiet = false) : ITenantEvent
{
    public int Version => 1;
}

/// <param name="Quiet">
/// Set by a feed sync for changes that are not news: refreshing an item already live, dropping
/// one the feed no longer has, a first import. Caches and search still act on it; nobody is notified.
/// </param>
public sealed record ContentUnpublished(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid PluginInstanceId,
    Guid ContentItemId,
    string ContentType,
    string Slug,
    bool Quiet = false) : ITenantEvent
{
    public int Version => 1;
}
