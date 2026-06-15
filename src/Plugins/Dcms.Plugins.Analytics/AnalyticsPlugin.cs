using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Analytics;

public sealed class AnalyticsPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "analytics",
        name: "Analytics",
        description: "Website analytics collection, rollups and dashboards.",
        allowMultipleInstances: false);

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
