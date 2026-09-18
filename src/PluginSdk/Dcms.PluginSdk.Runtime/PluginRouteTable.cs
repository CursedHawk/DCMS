using Dcms.PluginSdk.Abstractions;

namespace Dcms.PluginSdk.Runtime;

/// <summary>
/// Records the content routes a plugin declares (via MapContentList /
/// MapContentGetBySlug) so the delivery runtime can serve them generically per
/// instance. Built once at startup from the registry.
/// </summary>
public sealed class PluginRouteTable
{
    private readonly Dictionary<string, Dictionary<string, ContentRouteInfo>> _byPlugin = new(StringComparer.Ordinal);

    public PluginRouteTable(PluginRegistry registry)
    {
        foreach (var plugin in registry.Plugins)
        {
            var recorder = new RecordingEndpointBuilder();
            plugin.MapEndpoints(recorder);
            _byPlugin[plugin.Manifest.Id] = recorder.Routes;
        }
    }

    public ContentRouteInfo? Find(string pluginId, string contentType)
        => _byPlugin.TryGetValue(pluginId, out var routes) && routes.TryGetValue(contentType, out var info)
            ? info
            : null;
}

public sealed class ContentRouteInfo
{
    public bool List { get; set; }
    public bool GetBySlug { get; set; }
    public ContentQueryOptions Options { get; set; } = new();
}

/// <summary>
/// IPluginEndpointBuilder that records a plugin's content-route declarations instead of mounting
/// routes, so the delivery runtime can serve them generically per instance.
/// </summary>
internal sealed class RecordingEndpointBuilder : IPluginEndpointBuilder
{
    public Dictionary<string, ContentRouteInfo> Routes { get; } = new(StringComparer.Ordinal);

    public void MapContentList(string contentType, Action<ContentQueryOptions>? configure = null)
    {
        var info = GetOrAdd(contentType);
        info.List = true;
        configure?.Invoke(info.Options);
    }

    public void MapContentGetBySlug(string contentType) => GetOrAdd(contentType).GetBySlug = true;

    private ContentRouteInfo GetOrAdd(string contentType)
    {
        if (!Routes.TryGetValue(contentType, out var info))
        {
            info = new ContentRouteInfo();
            Routes[contentType] = info;
        }
        return info;
    }
}
