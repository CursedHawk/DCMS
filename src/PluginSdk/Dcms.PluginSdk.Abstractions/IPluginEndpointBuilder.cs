using Microsoft.AspNetCore.Builder;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// Endpoint surface handed to a plugin. Every route is implicitly tenant- and instance-scoped:
/// patterns are relative to <c>/api/{slug}</c> (site plane) or <c>/api/admin/plugins/{slug}</c>
/// (admin plane), and a request only reaches the handler when <c>{slug}</c> names an enabled
/// instance of <i>this</i> plugin for the tenant. Handlers take <c>IPluginContext</c> from DI.
/// </summary>
public interface IPluginEndpointBuilder
{
    /// <summary>Standard paged list endpoint for a content type (auto-documented).</summary>
    void MapContentList(string contentType, Action<ContentQueryOptions>? configure = null);

    /// <summary>Standard get-by-slug endpoint for a content type (auto-documented).</summary>
    void MapContentGetBySlug(string contentType);

    /// <summary>A bespoke route, e.g. <c>MapPost("/register", handler)</c>.</summary>
    RouteHandlerBuilder MapGet(string pattern, Delegate handler);

    RouteHandlerBuilder MapPost(string pattern, Delegate handler);

    RouteHandlerBuilder MapPut(string pattern, Delegate handler);

    RouteHandlerBuilder MapDelete(string pattern, Delegate handler);
}

public sealed class ContentQueryOptions
{
    public int DefaultPageSize { get; set; } = 20;
    public int MaxPageSize { get; set; } = 100;
    public string? DefaultOrderBy { get; set; }
}
