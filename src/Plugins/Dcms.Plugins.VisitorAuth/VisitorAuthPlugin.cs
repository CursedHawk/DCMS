using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.VisitorAuth;

public sealed class VisitorAuthPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "visitor-auth",
        name: "Visitor Authentication",
        description: "Visitor accounts (register, login, refresh) for the tenant website.",
        allowMultipleInstances: false,
        category: "Engagement",
        summary: "Accounts and gated content for site visitors.",
        iconName: "KeyRound");

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
