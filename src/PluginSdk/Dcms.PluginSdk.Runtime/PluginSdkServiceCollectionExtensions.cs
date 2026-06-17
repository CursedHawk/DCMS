using Dcms.PluginSdk.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Runtime;

public sealed class PluginRegistryBuilder
{
    internal List<IPlugin> Plugins { get; } = [];

    public PluginRegistryBuilder Add<TPlugin>()
        where TPlugin : IPlugin, new()
    {
        Plugins.Add(new TPlugin());
        return this;
    }
}

public static class PluginSdkServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsPlugins(this IServiceCollection services, Action<PluginRegistryBuilder> configure)
    {
        var registry = BuildRegistry(configure);
        services.AddSingleton(registry);
        services.AddSingleton<IPluginCatalog>(registry);
        services.AddSingleton<PluginRouteTable>();
        services.AddSingleton<OpenApiAssembler>();

        foreach (var plugin in registry.Plugins)
        {
            plugin.ConfigureServices(services);
        }

        return services;
    }

    /// <summary>
    /// Registers only the plugin manifest catalog (no ConfigureServices, no
    /// endpoints). Used by admin-api to drive config forms and the permission
    /// catalog without hosting plugin runtime services.
    /// </summary>
    public static IServiceCollection AddDcmsPluginCatalog(this IServiceCollection services, Action<PluginRegistryBuilder> configure)
    {
        var registry = BuildRegistry(configure);
        services.AddSingleton(registry);
        services.AddSingleton<IPluginCatalog>(registry);
        // The assembler only reads manifests + builds fragments (pure functions),
        // so it is safe to offer here without hosting plugin runtime services —
        // admin-api uses it to render the per-tenant OpenAPI preview.
        services.AddSingleton<OpenApiAssembler>();
        return services;
    }

    private static PluginRegistry BuildRegistry(Action<PluginRegistryBuilder> configure)
    {
        var builder = new PluginRegistryBuilder();
        configure(builder);
        return new PluginRegistry(builder.Plugins);
    }

    /// <summary>
    /// Phase 1 placeholder: exposes the registered manifests at /api/_plugins.
    /// Phase 4 replaces this with per-instance route mounting driven by the
    /// plugins.plugin_instances table.
    /// </summary>
    public static IEndpointRouteBuilder MapDcmsPlugins(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/_plugins", (PluginRegistry registry) =>
            Results.Ok(registry.Manifests.Select(m => new
            {
                m.Id,
                m.Name,
                m.Version,
                m.Description,
                m.AllowMultipleInstances,
            })));
        return app;
    }
}
