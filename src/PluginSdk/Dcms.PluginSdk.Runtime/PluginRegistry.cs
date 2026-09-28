using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime.Contracts;

namespace Dcms.PluginSdk.Runtime;

/// <summary>
/// Holds the statically registered plugin set and the contracts they (and the platform) provide.
/// Validates at startup, so a broken wiring fails the deploy rather than a request: ids unique
/// kebab-case, content type names unique within a plugin, contract shapes servable, every
/// required contract provided, no cycle of required contracts, subscriptions and jobs typed.
/// </summary>
public sealed class PluginRegistry : IPluginCatalog
{
    private readonly Dictionary<string, IPlugin> _plugins;
    private readonly Dictionary<string, RegisteredContract> _contracts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (EventDescriptor Event, ContractDescriptor Contract)> _events = new(StringComparer.Ordinal);

    public PluginRegistry(IEnumerable<IPlugin> plugins)
        : this(plugins, [])
    {
    }

    public PluginRegistry(IEnumerable<IPlugin> plugins, IEnumerable<ContractProvision> platformContracts)
    {
        _plugins = new Dictionary<string, IPlugin>(StringComparer.Ordinal);

        foreach (var plugin in plugins)
        {
            var manifest = plugin.Manifest;
            ValidateManifest(manifest);

            if (!_plugins.TryAdd(manifest.Id, plugin))
            {
                throw new InvalidOperationException($"Duplicate plugin id '{manifest.Id}'.");
            }
        }

        foreach (var provision in platformContracts)
        {
            AddContract(provision, providerPluginId: null);
        }
        foreach (var plugin in _plugins.Values)
        {
            foreach (var provision in plugin.Manifest.Provides ?? [])
            {
                AddContract(provision, plugin.Manifest.Id);
            }
        }

        foreach (var plugin in _plugins.Values)
        {
            ValidateConsumers(plugin.Manifest);
        }
        ValidateNoRequiredCycle();
    }

    public IReadOnlyCollection<IPlugin> Plugins => _plugins.Values;

    public IReadOnlyList<PluginManifest> Manifests => _plugins.Values.Select(p => p.Manifest).ToList();

    /// <summary>Every contract known to this host, platform and plugin-provided.</summary>
    public IReadOnlyCollection<RegisteredContract> Contracts => _contracts.Values;

    public PluginManifest? Find(string pluginId)
        => _plugins.TryGetValue(pluginId, out var plugin) ? plugin.Manifest : null;

    public IPlugin? FindPlugin(string pluginId)
        => _plugins.GetValueOrDefault(pluginId);

    public RegisteredContract? FindContract(string contractId)
        => _contracts.GetValueOrDefault(contractId);

    public RegisteredContract? FindContract(Type contractType)
        => _contracts.Values.FirstOrDefault(c => c.Descriptor.ContractType == contractType);

    /// <summary>The event with this name and the contract it is published under.</summary>
    public (EventDescriptor Event, ContractDescriptor Contract)? FindEvent(string eventName)
        => _events.TryGetValue(eventName, out var found) ? found : null;

    private void AddContract(ContractProvision provision, string? providerPluginId)
    {
        var descriptor = ContractDescriptorBuilder.Build(provision.Contract, providerPluginId);
        var impl = provision.Implementation;
        if (!impl.IsClass || impl.IsAbstract || !provision.Contract.IsAssignableFrom(impl))
        {
            throw new InvalidOperationException(
                $"'{impl.FullName}' must be a concrete class implementing {descriptor.Id}.");
        }
        if (!_contracts.TryAdd(descriptor.Id, new RegisteredContract(descriptor, impl)))
        {
            var existing = _contracts[descriptor.Id].Descriptor.ProviderPluginId ?? "the platform";
            throw new InvalidOperationException(
                $"Contract '{descriptor.Id}' is provided twice ({existing} and {providerPluginId ?? "the platform"}).");
        }
        foreach (var e in descriptor.Events)
        {
            if (!_events.TryAdd(e.Name, (e, descriptor)))
            {
                throw new InvalidOperationException(
                    $"Event '{e.Name}' is declared by both {_events[e.Name].Contract.Id} and {descriptor.Id}.");
            }
        }
    }

    private void ValidateConsumers(PluginManifest manifest)
    {
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in manifest.Consumes ?? [])
        {
            if (!ContractIds.IsValid(requirement.ContractId))
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' consumes '{requirement.ContractId}', which is not a valid contract id.");
            }
            if (!consumed.Add(requirement.ContractId))
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' declares consuming '{requirement.ContractId}' more than once.");
            }
            // A platform (dcms.*) contract is part of the SDK and every host registers it, so a
            // registry built without them (the manifest catalog, a test) still accepts a plugin
            // that needs one -- but only one the SDK actually defines, so a typo still fails.
            var satisfied = _contracts.ContainsKey(requirement.ContractId)
                || (ContractIds.IsPlatform(requirement.ContractId) && PlatformContractIds.Contains(requirement.ContractId));
            if (!requirement.Optional && !satisfied)
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' requires contract '{requirement.ContractId}', which nothing provides.");
            }
        }

        foreach (var subscription in manifest.Subscribes ?? [])
        {
            if (FindEvent(subscription.EventName) is not { } found)
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' subscribes to '{subscription.EventName}', which no contract declares.");
            }
            if (!consumed.Contains(found.Contract.Id))
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' subscribes to '{subscription.EventName}' but does not consume {found.Contract.Id}.");
            }
            if (found.Event.EventType != subscription.EventType)
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' subscribes to '{subscription.EventName}' with type {subscription.EventType.Name}, but the contract publishes {found.Event.EventType.Name}.");
            }
            var handlerInterface = typeof(IPluginEventHandler<>).MakeGenericType(subscription.EventType);
            if (!handlerInterface.IsAssignableFrom(subscription.Handler))
            {
                throw new InvalidOperationException(
                    $"'{subscription.Handler.FullName}' does not implement IPluginEventHandler<{subscription.EventType.Name}>.");
            }
        }

        var jobNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in manifest.Jobs ?? [])
        {
            if (string.IsNullOrWhiteSpace(job.Name) || !IsKebabCase(job.Name) || !jobNames.Add(job.Name))
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' job name '{job.Name}' must be unique kebab-case.");
            }
            if (!typeof(IPluginJobHandler).IsAssignableFrom(job.Handler))
            {
                throw new InvalidOperationException($"'{job.Handler.FullName}' does not implement IPluginJobHandler.");
            }
            if (job.Interval is { } interval && interval < TimeSpan.FromMinutes(1))
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' job '{job.Name}' runs more often than once a minute.");
            }
        }
    }

    /// <summary>
    /// A cycle of required contracts between plugins means neither can be enabled first. Optional
    /// edges are fine: the consumer degrades until the other side exists.
    /// </summary>
    private void ValidateNoRequiredCycle()
    {
        Dictionary<string, List<string>> edges = new(StringComparer.Ordinal);
        foreach (var plugin in _plugins.Values)
        {
            edges[plugin.Manifest.Id] = (plugin.Manifest.Consumes ?? [])
                .Where(r => !r.Optional)
                .Select(r => _contracts.GetValueOrDefault(r.ContractId)?.Descriptor.ProviderPluginId)
                .OfType<string>()
                .Where(p => p != plugin.Manifest.Id)
                .Distinct()
                .ToList();
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 visiting, 2 done
        var path = new Stack<string>();

        void Visit(string node)
        {
            state[node] = 1;
            path.Push(node);
            foreach (var next in edges.GetValueOrDefault(node) ?? [])
            {
                var s = state.GetValueOrDefault(next);
                if (s == 1)
                {
                    var cycle = path.Reverse().SkipWhile(n => n != next).Append(next);
                    throw new InvalidOperationException(
                        $"Plugins require each other's contracts in a cycle: {string.Join(" -> ", cycle)}.");
                }
                if (s == 0)
                {
                    Visit(next);
                }
            }
            path.Pop();
            state[node] = 2;
        }

        foreach (var node in edges.Keys)
        {
            if (state.GetValueOrDefault(node) == 0)
            {
                Visit(node);
            }
        }
    }

    private static void ValidateManifest(PluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || !IsKebabCase(manifest.Id))
        {
            throw new InvalidOperationException($"Plugin id '{manifest.Id}' must be non-empty kebab-case.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new InvalidOperationException($"Plugin '{manifest.Id}' must have a name.");
        }

        var duplicateType = manifest.ContentTypes
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateType is not null)
        {
            throw new InvalidOperationException(
                $"Plugin '{manifest.Id}' declares content type '{duplicateType.Key}' more than once.");
        }
    }

    /// <summary>Every <c>dcms.*</c> contract the SDK defines (in the Abstractions assembly).</summary>
    public static readonly IReadOnlySet<string> PlatformContractIds = typeof(IPlugin).Assembly.GetTypes()
        .Where(t => t.IsInterface)
        .Select(t => t.GetCustomAttributes(typeof(DcmsContractAttribute), false).FirstOrDefault() as DcmsContractAttribute)
        .Where(a => a is not null && ContractIds.IsPlatform(a.Id))
        .Select(a => a!.Id)
        .ToHashSet(StringComparer.Ordinal);

    public static bool IsKebabCase(string value)
        => value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
           && !value.StartsWith('-')
           && !value.EndsWith('-');
}

/// <summary>A contract known to this host and the class that implements it.</summary>
public sealed record RegisteredContract(ContractDescriptor Descriptor, Type Implementation);
