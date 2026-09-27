namespace Dcms.Shared.Contracts.Events;

/// <summary>
/// An event a plugin published under one of the contracts it provides, on
/// <c>plugins.events.{publisher}.{event}</c>. The payload is the event record as JSON; the
/// runtime validated it against the contract's event type before publishing, and each
/// subscriber deserialises it into the same type.
/// </summary>
public sealed record PluginEventPublished(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    string PublisherPluginId,
    string EventName,
    string PayloadJson) : ITenantEvent
{
    public int Version => 1;
}

/// <summary>
/// One run of a plugin job, on <c>plugins.jobs.{plugin}.{job}</c> (work queue). Enqueued by the
/// plugin itself (<c>dcms.jobs@1</c>) or by the interval scheduler.
/// </summary>
/// <param name="InstanceId">The instance the job is for, or null for tenant-wide work.</param>
/// <param name="NotBefore">A delayed job; the consumer defers it until then.</param>
public sealed record PluginJobRequested(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    string PluginId,
    string JobName,
    Guid? InstanceId,
    string? PayloadJson,
    DateTimeOffset? NotBefore) : ITenantEvent
{
    public int Version => 1;
}
