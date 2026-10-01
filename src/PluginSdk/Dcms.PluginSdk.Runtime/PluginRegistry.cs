using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Data;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Security;

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
    private readonly Dictionary<string, (HookDescriptor Hook, ContractDescriptor Contract)> _hooks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<(string PluginId, HookSubscription Subscription)>> _interceptors = new(StringComparer.Ordinal);

    public PluginRegistry(IEnumerable<IPlugin> plugins)
        : this(plugins, [])
    {
    }

    private readonly IReadOnlySet<string> _installed;

    /// <param name="installed">Ids of plugins loaded from the plugin directory rather than compiled in.</param>
    public PluginRegistry(
        IEnumerable<IPlugin> plugins, IEnumerable<ContractProvision> platformContracts, IReadOnlySet<string>? installed = null)
    {
        _installed = installed ?? new HashSet<string>();
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
            ValidateContentEvents(plugin.Manifest);
        }
        ValidateNoRequiredCycle();

        foreach (var group in _plugins.Values
                     .SelectMany(p => (p.Manifest.Intercepts ?? []).Select(s => (p.Manifest.Id, s)))
                     .GroupBy(x => x.s.HookName))
        {
            // Highest priority first; ties broken by plugin id so the order never depends on
            // registration order.
            _interceptors[group.Key] = group
                .OrderByDescending(x => x.s.Priority).ThenBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => (x.Id, x.s)).ToList();
        }
    }

    public IReadOnlyCollection<IPlugin> Plugins => _plugins.Values;

    /// <summary>"installed" for an operator-installed plugin, "builtin" for one compiled in.</summary>
    public string SourceOf(string pluginId) => _installed.Contains(pluginId) ? "installed" : "builtin";

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

    /// <summary>The hook with this name and the contract declaring it.</summary>
    public (HookDescriptor Hook, ContractDescriptor Contract)? FindHook(string hookName)
        => _hooks.TryGetValue(hookName, out var found) ? found : null;

    /// <summary>Who intercepts a hook, in the order they run.</summary>
    public IReadOnlyList<(string PluginId, HookSubscription Subscription)> InterceptorsOf(string hookName)
        => _interceptors.GetValueOrDefault(hookName) ?? [];

    /// <summary>The event with this name and the contract it is published under.</summary>
    public (EventDescriptor Event, ContractDescriptor Contract)? FindEvent(string eventName)
        => _events.TryGetValue(eventName, out var found) ? found : null;

    private void AddContract(ContractProvision provision, string? providerPluginId)
    {
        var impl = provision.Implementation;
        var descriptor = ContractDescriptorBuilder.Build(provision.Contract);
        var id = descriptor.Id;
        if (ContractIds.IsPlatform(id) != (providerPluginId is null))
        {
            throw new InvalidOperationException(providerPluginId is null
                ? $"'{id}' is registered as a platform contract but is not in the dcms.* namespace."
                : $"'{id}' is reserved for the platform; plugin '{providerPluginId}' cannot provide it.");
        }
        if (!impl.IsClass || impl.IsAbstract || !provision.Contract.IsAssignableFrom(impl))
        {
            throw new InvalidOperationException($"'{impl.FullName}' must be a concrete class implementing {id}.");
        }

        // Contracts are open: several plugins may implement one (payment gateways, map
        // providers), and consumers pick an instance or take them all. What must hold is that
        // they all mean the same interface -- two assemblies each declaring "x@1" would describe
        // different shapes under one id -- and that the platform's own contracts stay the
        // platform's alone.
        if (_contracts.TryGetValue(id, out var existing))
        {
            if (existing.Descriptor.ContractType != provision.Contract)
            {
                throw new InvalidOperationException(
                    $"Contract '{id}' is declared by two different interfaces ({existing.Descriptor.ContractType.FullName} and {provision.Contract.FullName}); " +
                    "implementers must reference the one declaring assembly.");
            }
            if (existing.Descriptor.IsPlatform || existing.ProvidedBy(providerPluginId!) is not null)
            {
                throw new InvalidOperationException(
                    $"Contract '{id}' is provided twice by {providerPluginId ?? "the platform"}.");
            }
            existing.Providers.Add(new ContractProvider(providerPluginId, impl));
            return;
        }

        _contracts[id] = new RegisteredContract(descriptor, [new ContractProvider(providerPluginId, impl)]);
        foreach (var e in descriptor.Events)
        {
            if (!_events.TryAdd(e.Name, (e, descriptor)))
            {
                throw new InvalidOperationException(
                    $"Event '{e.Name}' is declared by both {_events[e.Name].Contract.Id} and {descriptor.Id}.");
            }
        }
        foreach (var h in descriptor.Hooks)
        {
            if (!_hooks.TryAdd(h.Name, (h, descriptor)))
            {
                throw new InvalidOperationException(
                    $"Hook '{h.Name}' is declared by both {_hooks[h.Name].Contract.Id} and {descriptor.Id}.");
            }
        }
    }

    /// <summary>A content type's lifecycle events must be ContentChanged records the plugin publishes.</summary>
    private void ValidateContentEvents(PluginManifest manifest)
    {
        foreach (var type in manifest.ContentTypes)
        {
            foreach (var eventType in new[] { type.Published, type.Unpublished }.OfType<Type>())
            {
                if (!typeof(ContentChanged).IsAssignableFrom(eventType)
                    || eventType.GetConstructor([typeof(Guid), typeof(Guid), typeof(string), typeof(string)]) is null)
                {
                    throw new InvalidOperationException(
                        $"Plugin '{manifest.Id}' content type '{type.Name}': {eventType.Name} must be a ContentChanged record " +
                        "with the (InstanceId, ItemId, ContentType, Slug) constructor.");
                }
                var name = ContractIds.EventName(eventType);
                if (FindEvent(name) is not { } found
                    || FindContract(found.Contract.Id)?.ProvidedBy(manifest.Id) is null)
                {
                    throw new InvalidOperationException(
                        $"Plugin '{manifest.Id}' content type '{type.Name}' raises '{name}', which no contract it provides declares.");
                }
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
            // A provider may handle its own events (work moved off the request path) without
            // declaring it consumes itself.
            if (!consumed.Contains(found.Contract.Id) && FindContract(found.Contract.Id)?.ProvidedBy(manifest.Id) is null)
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

        foreach (var interception in manifest.Intercepts ?? [])
        {
            if (FindHook(interception.HookName) is not { } found)
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' intercepts '{interception.HookName}', which no contract declares.");
            }
            if (!consumed.Contains(found.Contract.Id))
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' intercepts '{interception.HookName}' but does not consume {found.Contract.Id}.");
            }
            if (found.Hook.HookType != interception.HookType)
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' intercepts '{interception.HookName}' with type {interception.HookType.Name}, but the contract declares {found.Hook.HookType.Name}.");
            }
            var handlerInterface = typeof(IPluginHookHandler<>).MakeGenericType(interception.HookType);
            if (!handlerInterface.IsAssignableFrom(interception.Handler))
            {
                throw new InvalidOperationException(
                    $"'{interception.Handler.FullName}' does not implement IPluginHookHandler<{interception.HookType.Name}>.");
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

        var screenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in manifest.AdminScreens ?? [])
        {
            if (string.IsNullOrWhiteSpace(screen.Id) || !IsKebabCase(screen.Id) || !screenIds.Add(screen.Id))
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' admin screen id '{screen.Id}' must be unique kebab-case.");
            }
            if (screen.Nav is { } nav && nav.Group is not ("main" or "build" or "admin" or "plugins"))
            {
                // The sidebar draws these sections only; an entry in any other is never seen.
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' admin screen '{screen.Id}' goes in menu section '{nav.Group}'; use main, build, admin or plugins.");
            }
            if (string.IsNullOrWhiteSpace(screen.Title))
            {
                throw new InvalidOperationException($"Plugin '{manifest.Id}' admin screen '{screen.Id}' needs a title.");
            }
            // A key nobody declares can be held by no role: the screen would be invisible to everyone.
            if (screen.Permission is { } permission && !DeclaresPermission(manifest, permission))
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' admin screen '{screen.Id}' names permission '{permission}', which neither the plugin nor the platform declares.");
            }
        }

        var dataSetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in manifest.DataSets ?? [])
        {
            // Kebab-case keeps plugin ids apart from the platform's, which are contract ids ("dcms.storage").
            if (string.IsNullOrWhiteSpace(set.Id) || !IsKebabCase(set.Id) || !dataSetIds.Add(set.Id))
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' data set id '{set.Id}' must be unique kebab-case.");
            }
            if (!typeof(IPluginDataSet).IsAssignableFrom(set.Implementation)
                || set.Implementation.IsAbstract || set.Implementation.IsInterface)
            {
                throw new InvalidOperationException(
                    $"'{set.Implementation.FullName}' must be a concrete class implementing IPluginDataSet.");
            }
            foreach (var permission in new[] { set.ReadPermission, set.WritePermission }.OfType<string>())
            {
                // Its own, or a platform one (Forms reviews submissions under content:read).
                if (!DeclaresPermission(manifest, permission))
                {
                    throw new InvalidOperationException(
                        $"Plugin '{manifest.Id}' data set '{set.Id}' names permission '{permission}', which neither the plugin nor the platform declares.");
                }
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
                .SelectMany(r => _contracts.GetValueOrDefault(r.ContractId)?.ProviderPluginIds ?? [])
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

    /// <summary>A permission reference (bare action or full key) the plugin or the platform declares.</summary>
    public static bool DeclaresPermission(PluginManifest manifest, string permission)
    {
        var key = PluginPermissions.Resolve(manifest.Id, permission);
        return PlatformPermissions.All.Contains(key)
               || manifest.Permissions.Any(p => PluginPermissions.Resolve(manifest.Id, p.Action) == key);
    }

    private static void ValidateManifest(PluginManifest manifest)
    {
        var actions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permission in manifest.Permissions)
        {
            if (string.IsNullOrWhiteSpace(permission.Action) || !IsKebabCase(permission.Action) || !actions.Add(permission.Action))
            {
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' permission '{permission.Action}' must be unique kebab-case.");
            }
        }

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

/// <summary>A class implementing a contract; <see cref="PluginId"/> is null for the platform.</summary>
public sealed record ContractProvider(string? PluginId, Type Implementation);

/// <summary>A contract known to this host and every plugin (or the platform) implementing it.</summary>
public sealed record RegisteredContract(ContractDescriptor Descriptor, List<ContractProvider> Providers)
{
    public IEnumerable<string> ProviderPluginIds => Providers.Select(p => p.PluginId).OfType<string>();

    public ContractProvider? ProvidedBy(string pluginId) => Providers.FirstOrDefault(p => p.PluginId == pluginId);

    /// <summary>The host's implementation of a platform contract.</summary>
    public Type PlatformImplementation => Descriptor.IsPlatform
        ? Providers[0].Implementation
        : throw new InvalidOperationException($"{Descriptor.Id} is not a platform contract.");
}
