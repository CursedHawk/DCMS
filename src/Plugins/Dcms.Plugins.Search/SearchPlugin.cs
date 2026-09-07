using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Search;

public sealed class SearchPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "search",
        name: "Sitewide Search",
        description: "Full-text search across all searchable plugin content of the tenant.",
        allowMultipleInstances: false,
        category: "Engagement",
        summary: "Full-text search across published content.",
        iconName: "Search");

    public void ConfigureServices(IServiceCollection services)
    {
        // Plugin services arrive in later phases.
    }

    public void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        // Delivery endpoints arrive in later phases.
    }

    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
        => OpenApiFragment.Empty;
}
