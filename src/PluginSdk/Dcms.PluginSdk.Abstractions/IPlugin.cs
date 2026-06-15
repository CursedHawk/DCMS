using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// Contract every DCMS plugin implements. Plugins are statically registered at
/// content-api startup via AddDcmsPlugins; there is no runtime DLL loading.
/// </summary>
public interface IPlugin
{
    PluginManifest Manifest { get; }

    /// <summary>Register plugin-specific services. Called once at startup.</summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// Map delivery endpoints. Routes are mounted under /api/{instanceSlug}
    /// per enabled instance, with tenant and instance already resolved.
    /// </summary>
    void MapEndpoints(IPluginEndpointBuilder endpoints);

    /// <summary>
    /// Contribute the OpenAPI fragment for one configured instance. The
    /// admin-authored instance description is injected into the tag and every
    /// operation description so AI consumers see the intent of this instance.
    /// </summary>
    OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance);
}
