using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.LiveChat;

public sealed class LiveChatPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "live-chat",
        name: "Live Chat",
        description: "Live chat between website visitors and tenant agents.",
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
