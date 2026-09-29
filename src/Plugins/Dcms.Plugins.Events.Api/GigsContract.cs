using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Events.Api;

/// <summary>The fields of a published <c>gig</c>, as authored.</summary>
public sealed record Gig(string Title, string? Date, string Venue, string? Location, string? Attendees, string? Description, IReadOnlyList<string>? Genres, JsonElement? Photos, JsonElement? Performers);

[ContractEvent("events.gig.published")]
public sealed record GigPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("events.gig.unpublished")]
public sealed record GigUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Published events (gigs) of an Events instance. Drafts never leave the plugin.</summary>
[DcmsContract("events.gigs", 1, Description = "Published events (gigs) of an Events instance.",
    Events = [typeof(GigPublished), typeof(GigUnpublished)])]
public interface IGigs
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "Published gigs, newest first.")]
    Task<ContentPage<Gig>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "One published gig by slug.")]
    Task<ContentEntry<Gig>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}
