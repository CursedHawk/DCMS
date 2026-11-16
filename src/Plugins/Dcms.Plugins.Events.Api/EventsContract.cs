using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Events.Api;

/// <summary>The fields of a published <c>event</c>, as authored.</summary>
public sealed record Event(string Title, string? Date, string Venue, string? Location, string? Attendees, string? Description, IReadOnlyList<string>? Genres, JsonElement? Photos, JsonElement? Performers);

[ContractEvent("events.event.published")]
public sealed record EventPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("events.event.unpublished")]
public sealed record EventUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Published events of an Events instance. Drafts never leave the plugin.</summary>
[DcmsContract("events.events", 1, Description = "Published events of an Events instance.",
    Events = [typeof(EventPublished), typeof(EventUnpublished)])]
public interface IEvents
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai | OpExposure.Link,
        Description = "Published events, newest first.")]
    Task<ContentPage<Event>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai | OpExposure.Link,
        Description = "One published event by slug.")]
    Task<ContentEntry<Event>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}
