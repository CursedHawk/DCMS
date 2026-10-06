using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// Contract every DCMS plugin implements. The built-in set is registered statically
/// (<c>Dcms.Plugins.All</c>); an operator can add more by dropping a plugin's folder into the
/// host's plugin directory (<c>Plugins:Directory</c>). Only <see cref="Manifest"/> is required —
/// every other member has a default, so a content-only plugin is a manifest and nothing else.
/// </summary>
public interface IPlugin
{
    PluginManifest Manifest { get; }

    /// <summary>
    /// Register plugin services. Called once per host at startup; <paramref name="host"/> says
    /// which host (the public site plane or the admin plane) and carries the operator's settings
    /// for this plugin (<c>Plugins:{id}:*</c>), so a plugin registers only what that host runs.
    /// </summary>
    void ConfigureServices(IServiceCollection services, PluginHost host)
    {
    }

    /// <summary>
    /// Map site-plane routes, mounted by content-api under <c>/api/{instanceSlug}</c> per enabled
    /// instance, with tenant and instance already resolved. By default every declared content
    /// type gets the standard list and get-by-slug routes.
    /// </summary>
    void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        foreach (var type in Manifest.ContentTypes)
        {
            endpoints.MapContentList(type.Name);
            endpoints.MapContentGetBySlug(type.Name);
        }
    }

    /// <summary>
    /// Map admin-plane routes, mounted by admin-api under <c>/api/admin/plugins/{instanceSlug}</c>
    /// for authenticated tenant members. Content-route declarations are not available here.
    /// </summary>
    void MapAdminEndpoints(IPluginEndpointBuilder endpoints)
    {
    }

    /// <summary>
    /// Routes that do not belong to one instance — a SignalR hub, an OAuth callback a provider
    /// redirects to. Mapped on the host's root with no instance filter, so the handler resolves
    /// tenant and instance itself. Prefer <see cref="MapEndpoints"/> whenever a slug fits.
    /// </summary>
    void MapHostEndpoints(IEndpointRouteBuilder app, PluginHost host)
    {
    }

    /// <summary>
    /// Contribute the OpenAPI fragment for one configured instance. The admin-authored instance
    /// description is injected into the tag and every operation description so AI consumers see
    /// the intent of this instance. By default: list + get for every declared content type.
    /// </summary>
    OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
        => ContentApiFragment.ForContentTypes(instance, Manifest);

    /// <summary>
    /// The fragment for a plugin whose API is runtime data rather than instance config — tables a
    /// tenant defined, read from the plugin's own storage. Gets the request's services (tenant
    /// already resolved). By default, <see cref="BuildOpenApiFragment"/>.
    /// </summary>
    Task<OpenApiFragment> BuildOpenApiFragmentAsync(PluginInstanceContext instance, IServiceProvider services, CancellationToken ct)
        => Task.FromResult(BuildOpenApiFragment(instance));

    /// <summary>
    /// What changes the instance's API besides its config, e.g. the hash of a published model.
    /// Part of every cache key over the generated document and clients, so they are rebuilt the
    /// moment it changes. Null (the default) when config is all there is.
    /// </summary>
    Task<string?> ApiVersionAsync(PluginInstanceContext instance, IServiceProvider services, CancellationToken ct)
        => Task.FromResult<string?>(null);
}
