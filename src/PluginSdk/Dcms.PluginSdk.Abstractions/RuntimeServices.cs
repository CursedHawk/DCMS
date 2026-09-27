namespace Dcms.PluginSdk.Abstractions;

/// <summary>Resolves a media asset id to its variant URLs (webp ladder, HLS master, download).</summary>
public interface IMediaResolver
{
    Task<MediaAssetDto?> ResolveAsync(Guid assetId, CancellationToken ct = default);
}

/// <summary>
/// Projects a published content item into the sitewide search index. Return
/// null to exclude the item. Called by the search indexer on content.published.
/// </summary>
public interface ISearchContributor
{
    SearchDocument? Project(ContentItemDto item, PluginInstanceContext instance);
}

/// <summary>Marker for event records published under a contract (see <c>[ContractEvent]</c>).</summary>
public interface IPluginEvent;

/// <summary>
/// Read-only manifest catalog. Referenced by admin-api (config forms,
/// permission catalog) without loading plugin runtime code.
/// </summary>
public interface IPluginCatalog
{
    IReadOnlyList<PluginManifest> Manifests { get; }
    PluginManifest? Find(string pluginId);
}
