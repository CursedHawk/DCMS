using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Roster.Api;

/// <summary>The fields of a published <c>member</c>, as authored.</summary>
public sealed record RosterMember(string Name, string? Role, string? Bio, Guid? Photo, JsonElement? Custom);

[ContractEvent("roster.member.published")]
public sealed record RosterMemberPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("roster.member.unpublished")]
public sealed record RosterMemberUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Published members of a Roster instance. Drafts never leave the plugin.</summary>
[DcmsContract("roster.members", 2, Description = "Published members of a Roster instance.",
    Events = [typeof(RosterMemberPublished), typeof(RosterMemberUnpublished)])]
public interface IRosterMembers
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "Published members, newest first.")]
    Task<ContentPage<RosterMember>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "One published member by slug.")]
    Task<ContentEntry<RosterMember>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}
