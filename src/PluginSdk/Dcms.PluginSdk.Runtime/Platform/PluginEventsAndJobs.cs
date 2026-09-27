using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Messaging;

namespace Dcms.PluginSdk.Runtime.Platform;

/// <summary>
/// <see cref="IPluginEvents"/>: a plugin may only publish events declared by a contract it
/// provides, with a payload that deserialises into that event's record. Anything else would let
/// one plugin forge another's events to every subscriber.
/// </summary>
public sealed class PluginEvents(IPluginContext caller, PluginRegistry registry, IEventPublisher bus) : IPluginEvents
{
    public Task PublishAsync(EventPublish input, CancellationToken ct)
    {
        if (registry.FindEvent(input.Name) is not { } found || found.Contract.ProviderPluginId != caller.PluginId)
        {
            throw new ContractValidationException(
                $"Plugin '{caller.PluginId}' does not provide a contract declaring event '{input.Name}'.");
        }
        try
        {
            if (input.Payload.Deserialize(found.Event.EventType, ContractDescriptorBuilder.Json) is null)
            {
                throw new ContractValidationException($"Event '{input.Name}' has an empty payload.");
            }
        }
        catch (JsonException e)
        {
            throw new ContractValidationException($"Event '{input.Name}' payload does not match its contract: {e.Message}");
        }

        var message = new PluginEventPublished(
            Guid.CreateVersion7(), DateTimeOffset.UtcNow, caller.TenantId, caller.PluginId, input.Name, input.Payload.GetRawText());
        return bus.PublishAsync(
            Subjects.PluginEvent(caller.PluginId, input.Name), message, ct, messageId: message.EventId.ToString()).AsTask();
    }
}

/// <summary><see cref="IPluginJobs"/>: enqueue one of the caller's own declared jobs.</summary>
public sealed class PluginJobs(IPluginContext caller, PluginRegistry registry, IEventPublisher bus) : IPluginJobs
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromDays(1);

    public Task EnqueueAsync(JobEnqueue input, CancellationToken ct)
    {
        var declared = registry.Find(caller.PluginId)?.Jobs?.Any(j => j.Name == input.Name) == true;
        if (!declared)
        {
            throw new ContractValidationException($"Plugin '{caller.PluginId}' declares no job '{input.Name}'.");
        }
        var instanceId = input.InstanceId ?? caller.Instance?.InstanceId;
        if (input.InstanceId is { } requested && caller.Instance is { } own && requested != own.InstanceId)
        {
            throw new ContractValidationException("A job can only be enqueued for the instance being served.");
        }
        if (input.DedupeKey is { Length: > 200 })
        {
            throw new ContractValidationException("DedupeKey is at most 200 characters.");
        }
        var payload = input.Payload?.GetRawText();
        if (payload is { Length: > 64 * 1024 })
        {
            throw new ContractValidationException("Job payload is at most 64 KiB; store bulk data with dcms.storage and pass its key.");
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? notBefore = input.DelaySeconds is > 0 and var s
            ? now + TimeSpan.FromSeconds(Math.Min(s, MaxDelay.TotalSeconds))
            : null;
        var message = new PluginJobRequested(
            Guid.CreateVersion7(), now, caller.TenantId, caller.PluginId, input.Name, instanceId, payload, notBefore);
        var messageId = input.DedupeKey is { } key
            ? $"{caller.TenantId}:{caller.PluginId}:{input.Name}:{key}"
            : message.EventId.ToString();
        return bus.PublishAsync(Subjects.PluginJob(caller.PluginId, input.Name), message, ct, messageId).AsTask();
    }
}
