namespace Dcms.PluginSdk.Abstractions;

/// <summary>Resolves a media asset id to its variant URLs (webp ladder, HLS master, download).</summary>
public interface IMediaResolver
{
    Task<MediaAssetDto?> ResolveAsync(Guid assetId, CancellationToken ct = default);
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
