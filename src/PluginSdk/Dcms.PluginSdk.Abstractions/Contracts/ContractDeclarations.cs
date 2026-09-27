using System.Text.Json;
using Dcms.Shared.Kernel.Abstractions;

namespace Dcms.PluginSdk.Abstractions.Contracts;

/// <summary>A contract a plugin provides, and the class that implements it.</summary>
public sealed record ContractProvision(Type Contract, Type Implementation)
{
    public static ContractProvision Of<TContract, TImplementation>()
        where TContract : class
        where TImplementation : class, TContract
        => new(typeof(TContract), typeof(TImplementation));
}

/// <summary>
/// A contract a plugin consumes. This list is the plugin's grant: <see cref="IPluginContracts.Get{T}"/>
/// refuses anything not declared here, even in-process, so the manifest stays a truthful
/// statement of what a plugin can reach.
/// </summary>
/// <param name="Optional">The plugin works without a provider; <see cref="IPluginContracts.TryGet{T}"/> returns null.</param>
/// <param name="BindingConfigKey">
/// For a provider with several instances: the consumer's config key holding the chosen provider
/// instance id. Its JSON Schema property carries <c>"x-dcms-contract-binding": "&lt;contractId&gt;"</c>
/// so the admin form renders a picker.
/// </param>
public sealed record ContractRequirement(string ContractId, bool Optional = false, string? BindingConfigKey = null)
{
    public static ContractRequirement Of<TContract>(bool optional = false, string? bindingConfigKey = null)
        => new(ContractIds.Of<TContract>(), optional, bindingConfigKey);
}

/// <summary>A handler for an event published under a contract the plugin consumes.</summary>
public sealed record EventSubscription(string EventName, Type EventType, Type Handler)
{
    public static EventSubscription Of<TEvent, THandler>()
        where TEvent : IPluginEvent
        where THandler : IPluginEventHandler<TEvent>
        => new(ContractIds.EventName(typeof(TEvent)), typeof(TEvent), typeof(THandler));
}

/// <summary>
/// A named background job. With an <see cref="Interval"/> the platform scheduler runs it for
/// every tenant with an enabled instance; without one it only runs when enqueued.
/// </summary>
public sealed record JobDeclaration(string Name, Type Handler, TimeSpan? Interval = null)
{
    public static JobDeclaration Of<THandler>(string name, TimeSpan? interval = null)
        where THandler : IPluginJobHandler
        => new(name, typeof(THandler), interval);
}

/// <summary>Who a plugin call is being made for.</summary>
public sealed record PluginActor(ActorKind Kind, Guid? Id = null, string? Key = null, bool IsSuperAdmin = false)
{
    public static readonly PluginActor Anonymous = new(ActorKind.Anonymous);
    public static readonly PluginActor System = new(ActorKind.System);

    public static PluginActor From(ICurrentActor actor) =>
        new(actor.Kind, actor.Id, actor.Key, actor.IsSuperAdmin);
}

/// <summary>
/// Everything plugin code needs to know about where it is running, and its only way to reach
/// another plugin's or the platform's contracts. Scoped: one per request, message or job.
/// </summary>
public interface IPluginContext
{
    Guid TenantId { get; }

    /// <summary>The plugin this context belongs to — the caller whose <c>Consumes</c> gates <see cref="Contracts"/>.</summary>
    string PluginId { get; }

    /// <summary>The instance being served; null for tenant-wide work (a scheduled job, an event handler).</summary>
    PluginInstanceContext? Instance { get; }

    PluginActor Actor { get; }

    IPluginContracts Contracts { get; }
}

public interface IPluginContracts
{
    /// <summary>
    /// Resolves a contract. Throws when the caller did not declare it in <c>Consumes</c>, or no
    /// enabled provider instance exists for this tenant.
    /// </summary>
    /// <param name="providerInstanceId">Pick a provider instance explicitly; otherwise the
    /// requirement's binding config key, then the sole enabled instance, decide.</param>
    T Get<T>(Guid? providerInstanceId = null) where T : class;

    /// <summary>As <see cref="Get{T}"/>, but null when no enabled provider instance exists.</summary>
    T? TryGet<T>(Guid? providerInstanceId = null) where T : class;
}

public interface IPluginEventHandler<in TEvent> where TEvent : IPluginEvent
{
    Task HandleAsync(TEvent @event, IPluginContext context, CancellationToken ct);
}

public interface IPluginJobHandler
{
    Task RunAsync(JsonElement? payload, IPluginContext context, CancellationToken ct);
}
