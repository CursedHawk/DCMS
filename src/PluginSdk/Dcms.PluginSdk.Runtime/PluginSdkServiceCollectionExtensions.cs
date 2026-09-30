using Dcms.PluginSdk.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Dcms.PluginSdk.Runtime.Platform;

namespace Dcms.PluginSdk.Runtime;

public sealed class PluginRegistryBuilder(PluginHost host)
{
    internal List<IPlugin> Plugins { get; } = [];
    internal List<ContractProvision> PlatformContracts { get; } = [];

    /// <summary>The host the registry is being built for.</summary>
    public PluginHost Host { get; } = host;

    public PluginRegistryBuilder Add<TPlugin>()
        where TPlugin : IPlugin, new()
        => Add(new TPlugin());

    public PluginRegistryBuilder Add(IPlugin plugin)
    {
        Plugins.Add(plugin);
        return this;
    }

    /// <summary>
    /// Registers a platform (<c>dcms.*</c>) contract this host implements. The implementation is
    /// constructed per resolution with the <i>calling</i> plugin's <see cref="IPluginContext"/>,
    /// so it can stamp that plugin's tenant and id onto everything it touches.
    /// </summary>
    /// <remarks>
    /// The first registration of a contract wins. A host that implements one differently — the
    /// admin plane's <c>dcms.media@1</c> can import files, the site plane's cannot — registers
    /// its own in the configure callback, before the defaults are added.
    /// </remarks>
    public PluginRegistryBuilder AddPlatformContract<TContract, TImplementation>()
        where TContract : class
        where TImplementation : class, TContract
    {
        if (!PlatformContracts.Any(p => p.Contract == typeof(TContract)))
        {
            PlatformContracts.Add(ContractProvision.Of<TContract, TImplementation>());
        }
        return this;
    }
}

public static class PluginSdkServiceCollectionExtensions
{
    /// <summary>
    /// Hosts plugins in a service: the registry built for <paramref name="plane"/> with that
    /// plane's platform contracts, every plugin's services, and the contract runtime.
    /// </summary>
    public static IHostApplicationBuilder AddDcmsPlugins(
        this IHostApplicationBuilder builder, PluginPlane plane, Action<PluginRegistryBuilder> configure)
    {
        var host = new PluginHost(plane, builder.Configuration, builder.Environment.EnvironmentName);
        builder.Services.AddDcmsPlugins(host, plugins =>
        {
            configure(plugins);
            plugins.AddPlatformContracts(plane);
        });
        return builder;
    }

    /// <summary>Registry without platform contracts, for tests and tools.</summary>
    public static IServiceCollection AddDcmsPlugins(this IServiceCollection services, Action<PluginRegistryBuilder> configure)
        => services.AddDcmsPlugins(PluginHost.Empty(), configure);

    public static IServiceCollection AddDcmsPlugins(
        this IServiceCollection services, PluginHost host, Action<PluginRegistryBuilder> configure)
    {
        var registry = BuildRegistry(host, configure);
        services.AddSingleton(host);
        services.AddSingleton(registry);
        services.AddSingleton<IPluginCatalog>(registry);
        services.AddSingleton<PluginRouteTable>();
        services.AddSingleton<OpenApiAssembler>();
        AddPluginContexts(services);

        foreach (var plugin in registry.Plugins)
        {
            plugin.ConfigureServices(services, host);
        }

        return services;
    }

    private static PluginRegistry BuildRegistry(PluginHost host, Action<PluginRegistryBuilder> configure)
    {
        var builder = new PluginRegistryBuilder(host);
        configure(builder);

        // The operator's off switch (Plugins:Disabled), the equivalent of removing a jar: the
        // plugin is not registered at all, so anything that requires its contracts fails startup
        // with a message naming both rather than failing later on a request.
        var disabled = host.Configuration.GetSection("Plugins:Disabled").GetChildren()
            .Select(c => c.Value).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return new PluginRegistry(
            builder.Plugins.Where(p => !disabled.Contains(p.Manifest.Id)), builder.PlatformContracts);
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
        app.MapDcmsPluginHostEndpoints();
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
