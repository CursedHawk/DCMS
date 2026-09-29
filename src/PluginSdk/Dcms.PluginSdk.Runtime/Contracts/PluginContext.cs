using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Runtime.Contracts;

/// <summary>
/// The tenant's enabled plugin instances, as the runtime sees them. Implemented over
/// <c>plugins.plugin_instances</c> (<see cref="CmsPluginInstanceStore"/>).
/// </summary>
public interface IPluginInstanceStore
{
    Task<IReadOnlyList<PluginInstanceContext>> ListEnabledAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>
/// Builds <see cref="IPluginContext"/>s. Scoped, and loads the tenant's enabled instances once
/// per scope: every contract resolution in a request answers from that one read.
/// </summary>
public sealed class PluginContextFactory(PluginRegistry registry, IPluginInstanceStore store, IServiceProvider services)
{
    private Guid? _loadedFor;
    private IReadOnlyList<PluginInstanceContext> _enabled = [];

    public async Task<IReadOnlyList<PluginInstanceContext>> EnabledInstancesAsync(Guid tenantId, CancellationToken ct)
    {
        if (_loadedFor != tenantId)
        {
            _enabled = await store.ListEnabledAsync(tenantId, ct);
            _loadedFor = tenantId;
        }
        return _enabled;
    }

    /// <summary>A context for <paramref name="pluginId"/>, optionally serving one of its instances.</summary>
    public async Task<IPluginContext> CreateAsync(
        Guid tenantId, string pluginId, PluginInstanceContext? instance, PluginActor actor, CancellationToken ct)
    {
        if (registry.Find(pluginId) is null)
        {
            throw new InvalidOperationException($"Unknown plugin '{pluginId}'.");
        }
        var enabled = await EnabledInstancesAsync(tenantId, ct);
        return new PluginContext(tenantId, pluginId, instance, actor, enabled, registry, services);
    }

    /// <summary>The actor of the current request or message, as plugins see it.</summary>
    public PluginActor CurrentActor() =>
        services.GetService<ICurrentActor>() is { } actor ? PluginActor.From(actor) : PluginActor.Anonymous;
}

/// <summary>
/// Holds the context an endpoint filter (or message handler) established for this scope, so
/// plugin handlers can take <see cref="IPluginContext"/> as a plain DI parameter.
/// </summary>
public sealed class PluginContextAccessor
{
    public IPluginContext? Current { get; set; }
}

/// <summary>
/// What DI hands out for <see cref="IPluginContext"/>. Minimal APIs bind handler parameters
/// before endpoint filters run, so the context cannot be captured at injection time; this view
/// reads the accessor on every member access instead, by which point the filter has set it.
/// </summary>
internal sealed class AmbientPluginContext(PluginContextAccessor accessor) : IPluginContext
{
    private IPluginContext Current => accessor.Current
        ?? throw new InvalidOperationException(
            "No plugin context in this scope: IPluginContext is only available inside a plugin route, event handler or job.");

    public Guid TenantId => Current.TenantId;
    public string PluginId => Current.PluginId;
    public PluginInstanceContext? Instance => Current.Instance;
    public PluginActor Actor => Current.Actor;
    public IPluginContracts Contracts => Current.Contracts;
}

internal sealed class PluginContext(
    Guid tenantId,
    string pluginId,
    PluginInstanceContext? instance,
    PluginActor actor,
    IReadOnlyList<PluginInstanceContext> enabled,
    PluginRegistry registry,
    IServiceProvider services) : IPluginContext, IPluginContracts
{
    public Guid TenantId => tenantId;
    public string PluginId => pluginId;
    public PluginInstanceContext? Instance => instance;
    public PluginActor Actor => actor;
    public IPluginContracts Contracts => this;

    public T Get<T>(Guid? providerInstanceId = null) where T : class =>
        Resolve<T>(providerInstanceId, required: true)!;

    public T? TryGet<T>(Guid? providerInstanceId = null) where T : class =>
        Resolve<T>(providerInstanceId, required: false);

    public IReadOnlyList<T> GetAll<T>() where T : class
    {
        var (registered, _) = Declared<T>();
        if (registered.Descriptor.IsPlatform)
        {
            return [Platform<T>(registered)];
        }
        return Candidates(registered).Select(i => Provide<T>(registered, i)).ToList();
    }

    private T? Resolve<T>(Guid? providerInstanceId, bool required) where T : class
    {
        var (registered, requirement) = Declared<T>();
        var descriptor = registered.Descriptor;
        if (descriptor.IsPlatform)
        {
            return Platform<T>(registered);
        }

        var provider = FindProviderInstance(registered, requirement, providerInstanceId);
        if (provider is null)
        {
            return required
                ? throw new InvalidOperationException(
                    $"No enabled instance of {string.Join(" or ", registered.ProviderPluginIds.Select(p => $"'{p}'"))} provides {descriptor.Id} for this tenant.")
                : null;
        }
        return Provide<T>(registered, provider);
    }

    /// <summary>The contract, and the caller's declaration of it — without which nothing resolves.</summary>
    private (RegisteredContract Registered, ContractRequirement Requirement) Declared<T>()
    {
        var registered = registry.FindContract(typeof(T))
            ?? throw new InvalidOperationException($"'{typeof(T).FullName}' is not a contract registered in this host.");
        var requirement = registry.Find(pluginId)!.Consumes?.FirstOrDefault(r => r.ContractId == registered.Descriptor.Id)
            ?? throw new InvalidOperationException(
                $"Plugin '{pluginId}' does not declare consuming {registered.Descriptor.Id}; add it to the manifest's Consumes.");
        return (registered, requirement);
    }

    // A platform contract acts for the caller: its implementation stamps the caller's tenant and
    // plugin id onto everything it touches.
    private T Platform<T>(RegisteredContract registered) where T : class =>
        ContractProxy.Create((T)Construct(registered.PlatformImplementation, this), registered.Descriptor, pluginId, null, services);

    // A plugin provider runs as itself, serving the chosen instance.
    private T Provide<T>(RegisteredContract registered, PluginInstanceContext provider) where T : class
    {
        var implementation = registered.ProvidedBy(provider.PluginId)!.Implementation;
        var providerContext = new PluginContext(tenantId, provider.PluginId, provider, actor, enabled, registry, services);
        return ContractProxy.Create((T)Construct(implementation, providerContext), registered.Descriptor, pluginId, provider.PluginId, services);
    }

    private object Construct(Type implementation, IPluginContext context) =>
        ContractActivator.Create(services, implementation, context);

    private List<PluginInstanceContext> Candidates(RegisteredContract registered) =>
        enabled.Where(i => registered.ProvidedBy(i.PluginId) is not null).ToList();

    private PluginInstanceContext? FindProviderInstance(
        RegisteredContract registered, ContractRequirement requirement, Guid? explicitId)
    {
        var candidates = Candidates(registered);
        if (explicitId is { } id)
        {
            return candidates.FirstOrDefault(i => i.InstanceId == id);
        }
        if (requirement.BindingConfigKey is { } key && BindingValue(key) is { } bound)
        {
            // An instance id or a slug. A binding that names a disabled or deleted instance
            // resolves to nothing rather than silently falling through to some other instance.
            return candidates.FirstOrDefault(i =>
                Guid.TryParse(bound, out var id) ? i.InstanceId == id : string.Equals(i.Slug, bound, StringComparison.Ordinal));
        }
        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new InvalidOperationException(
                $"Several instances provide {registered.Descriptor.Id}; " +
                $"plugin '{pluginId}' must bind one (BindingConfigKey), pass an instance id, or use GetAll."),
        };
    }

    private string? BindingValue(string configKey) =>
        instance?.Config.RootElement is { ValueKind: JsonValueKind.Object } root
        && root.TryGetProperty(configKey, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } bound
            ? bound
            : null;
}

/// <summary>Builds contract providers. They are never registered in DI, so nothing can reach one around the proxy.</summary>
internal static class ContractActivator
{
    /// <summary>Resolves the provider's dependencies from DI, handing it <paramref name="context"/> when its constructor asks for one.</summary>
    public static object Create(IServiceProvider services, Type implementation, IPluginContext context) =>
        implementation.GetConstructors()
            .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IPluginContext)))
            ? ActivatorUtilities.CreateInstance(services, implementation, context)
            : ActivatorUtilities.CreateInstance(services, implementation);
}
