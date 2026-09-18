namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// Endpoint surface handed to a plugin. Every route is implicitly tenant- and instance-scoped.
/// Plugins declare content list/get-by-slug routes; the delivery runtime serves them generically.
/// </summary>
public interface IPluginEndpointBuilder
{
    /// <summary>Standard paged list endpoint for a content type (auto-documented).</summary>
    void MapContentList(string contentType, Action<ContentQueryOptions>? configure = null);

    /// <summary>Standard get-by-slug endpoint for a content type (auto-documented).</summary>
    void MapContentGetBySlug(string contentType);
}

public sealed class ContentQueryOptions
{
    public int DefaultPageSize { get; set; } = 20;
    public int MaxPageSize { get; set; } = 100;
    public string? DefaultOrderBy { get; set; }
}
