using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.PluginSdk.Abstractions.Platform;

/// <summary>An event by name, with its record as JSON. Prefer <see cref="PluginContextExtensions.PublishAsync{TEvent}"/>.</summary>
public sealed record EventPublish(string Name, JsonElement Payload);

/// <summary>
/// Publishes events under the contracts the calling plugin provides. Subscribers (plugins whose
/// manifest <c>Subscribes</c> to the event and <c>Consumes</c> its contract) receive it in
/// admin-api, once each, as the event's tenant.
/// </summary>
[DcmsContract("dcms.events", 1, Description = "Publish events declared by the calling plugin's contracts.")]
public interface IPluginEvents
{
    /// <exception cref="ContractValidationException">The event is not declared by a contract the caller provides, or the payload does not match it.</exception>
    [Operation(OpRisk.Safe)]
    Task PublishAsync(EventPublish input, CancellationToken ct);
}

/// <param name="InstanceId">Defaults to the instance being served; null outside one for tenant-wide work.</param>
/// <param name="DelaySeconds">Run no sooner than this; at most one day.</param>
/// <param name="DedupeKey">Identifies the occurrence; a second enqueue with the same key within two minutes is dropped.</param>
public sealed record JobEnqueue(
    string Name,
    JsonElement? Payload = null,
    Guid? InstanceId = null,
    int? DelaySeconds = null,
    string? DedupeKey = null);

/// <summary>Enqueues one of the calling plugin's declared <c>Jobs</c>; admin-api runs it.</summary>
[DcmsContract("dcms.jobs", 1, Description = "Enqueue the calling plugin's background jobs.")]
public interface IPluginJobs
{
    [Operation(OpRisk.Safe)]
    Task EnqueueAsync(JobEnqueue input, CancellationToken ct);
}

public static class PluginContextExtensions
{
    /// <summary>Publishes a typed event (<c>[ContractEvent]</c> record) via <c>dcms.events@1</c>.</summary>
    public static Task PublishAsync<TEvent>(this IPluginContext context, TEvent @event, CancellationToken ct)
        where TEvent : IPluginEvent =>
        context.Contracts.Get<IPluginEvents>().PublishAsync(
            // The runtime type, so an event held as IPluginEvent still publishes under its own name.
            new EventPublish(ContractIds.EventName(@event.GetType()), JsonSerializer.SerializeToElement(@event, @event.GetType(), JsonSerializerOptions.Web)),
            ct);
}
