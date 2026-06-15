using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// Endpoint surface handed to a plugin. Wraps a RouteGroupBuilder mounted at
/// /api/{instanceSlug}; every route is implicitly tenant- and instance-scoped.
/// </summary>
public interface IPluginEndpointBuilder
{
    RouteGroupBuilder Group { get; }

    /// <summary>Standard paged list endpoint for a content type (auto-documented).</summary>
    void MapContentList(string contentType, Action<ContentQueryOptions>? configure = null);

    /// <summary>Standard get-by-slug endpoint for a content type (auto-documented).</summary>
    void MapContentGetBySlug(string contentType);

    RouteHandlerBuilder MapGet(string pattern, Delegate handler);
    RouteHandlerBuilder MapPost(string pattern, Delegate handler);
}

public sealed class ContentQueryOptions
{
    public int DefaultPageSize { get; set; } = 20;
    public int MaxPageSize { get; set; } = 100;
    public string? DefaultOrderBy { get; set; }
}
