using System.Text.Json.Nodes;

namespace Dcms.Plugins.DynamicApps.Api;

/// <summary>
/// Something that happened in an application, as automations see it: a runtime event, named
/// by string rather than by C# type, because the tables it is about are the tenant's own.
/// Written to the outbox in the same transaction as the change it describes (ADR 0021).
/// </summary>
/// <remarks>
/// Event names: <c>row.created</c>, <c>row.updated</c>, <c>row.deleted</c>,
/// <c>relation.created</c>, <c>relation.deleted</c>, <c>revision.published</c>,
/// <c>flow.event.{name}</c> (published by a flow).
/// </remarks>
public sealed record AppEvent
{
    public const string RowCreated = "row.created";
    public const string RowUpdated = "row.updated";
    public const string RowDeleted = "row.deleted";
    public const string RelationCreated = "relation.created";
    public const string RelationDeleted = "relation.deleted";
    public const string RevisionPublished = "revision.published";
    public const string FlowEventPrefix = "flow.event.";

    public Guid EventId { get; init; }
    public Guid TenantId { get; init; }
    public string SourcePlugin { get; init; } = DynamicAppsPermissions.PluginId;
    public Guid SourceInstanceId { get; init; }
    public string EventName { get; init; } = string.Empty;
    public int EventVersion { get; init; } = 1;
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>The published revision the event happened under.</summary>
    public int Revision { get; init; }

    /// <summary>What it is about: a table's api name and record id, or <c>app</c> for a publish.</summary>
    public AppEventEntity? Entity { get; init; }

    /// <summary>
    /// Row events: <c>record</c> (the record after the change; before it for a delete) and, for
    /// an update, <c>previous</c> (the changed fields' old values). Relation events: <c>relationship</c>,
    /// <c>sourceId</c>, <c>targetId</c>.
    /// </summary>
    public JsonObject Payload { get; init; } = [];

    /// <summary>Updates: the api names whose value changed, so "status changed" is cheap to test.</summary>
    public IReadOnlyList<string> ChangedFields { get; init; } = [];

    /// <summary>Shared by everything one original action set off, however many flows deep.</summary>
    public Guid CorrelationId { get; init; }

    /// <summary>The flow run whose write caused this event; null for a person's or the site's action.</summary>
    public Guid? CausationId { get; init; }

    /// <summary>How many flow runs deep this event is: 0 for a person's or the site's action.</summary>
    public int Depth { get; init; }
}

public sealed record AppEventEntity(string Type, Guid? Id);
