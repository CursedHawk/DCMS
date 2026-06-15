namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// Published-content reads scoped to the current plugin instance. Backed by
/// Redis with generation-counter invalidation (implemented in PluginSdk.Runtime).
/// </summary>
public interface IContentStore
{
    Task<ContentItemDto?> GetBySlugAsync(string contentType, string slug, CancellationToken ct = default);
    Task<PagedResult<ContentItemDto>> QueryAsync(string contentType, ContentQuery query, CancellationToken ct = default);
}

/// <summary>Resolves a media asset id to its variant URLs (webp ladder, HLS master, download).</summary>
public interface IMediaResolver
{
    Task<MediaAssetDto?> ResolveAsync(Guid assetId, CancellationToken ct = default);
}

/// <summary>Resolves ContentRef fields across instances of other plugins (published content only).</summary>
public interface IInterPluginResolver
{
    Task<ContentItemDto?> ResolveAsync(ContentRef reference, CancellationToken ct = default);
}

/// <summary>
/// Projects a published content item into the sitewide search index. Return
/// null to exclude the item. Called by the search indexer on content.published.
/// </summary>
public interface ISearchContributor
{
    SearchDocument? Project(ContentItemDto item, PluginInstanceContext instance);
}

/// <summary>Marker for plugin-published NATS events.</summary>
public interface IPluginEvent;

/// <summary>Publish typed events to NATS from plugin code.</summary>
public interface IPluginEventBus
{
    ValueTask PublishAsync<T>(T @event, CancellationToken ct = default)
        where T : IPluginEvent;
}

/// <summary>
/// Read-only manifest catalog. Referenced by admin-api (config forms,
/// permission catalog) without loading plugin runtime code.
/// </summary>
public interface IPluginCatalog
{
    IReadOnlyList<PluginManifest> Manifests { get; }
    PluginManifest? Find(string pluginId);
}
