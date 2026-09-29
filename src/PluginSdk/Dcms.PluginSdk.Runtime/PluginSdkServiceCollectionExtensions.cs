using Dcms.PluginSdk.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dcms.PluginSdk.Runtime;

public sealed class PluginRegistryBuilder
{
    internal List<IPlugin> Plugins { get; } = [];
    internal List<ContractProvision> PlatformContracts { get; } = [];

    public PluginRegistryBuilder Add<TPlugin>()
        where TPlugin : IPlugin, new()
    {
        Plugins.Add(new TPlugin());
        return this;
    }

    /// <summary>
    /// Registers a platform (<c>dcms.*</c>) contract this host implements. The implementation is
    /// constructed per resolution with the <i>calling</i> plugin's <see cref="IPluginContext"/>,
    /// so it can stamp that plugin's tenant and id onto everything it touches.
    /// </summary>
    public PluginRegistryBuilder AddPlatformContract<TContract, TImplementation>()
        where TContract : class
        where TImplementation : class, TContract
    {
        PlatformContracts.Add(ContractProvision.Of<TContract, TImplementation>());
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
        AddPluginContexts(services);

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
        return new PluginRegistry(builder.Plugins, builder.PlatformContracts);
    }

    private static void AddPluginContexts(IServiceCollection services)
    {
        services.TryAddScoped<IPluginInstanceStore, CmsPluginInstanceStore>();
        services.TryAddScoped<Platform.PublishedContentReader>();
        services.AddScoped<PluginContextFactory>();
        services.AddScoped<PluginContextAccessor>();
        services.AddScoped<ContractDispatcher>();
        services.AddScoped<IPluginContext, AmbientPluginContext>();
    }

    /// <summary>
    /// Mounts every plugin's site-plane routes (<see cref="PluginEndpoints"/>) and the manifest
    /// listing at /api/_plugins.
    /// </summary>
    public static IEndpointRouteBuilder MapDcmsPlugins(this IEndpointRouteBuilder app)
    {
        app.MapDcmsPluginSiteEndpoints();
        app.MapDcmsContractSiteEndpoints();
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
