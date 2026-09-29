using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;

namespace Dcms.Plugins.Roster;

public sealed record MemberQuery(int Page = 1, int PageSize = 50);

public sealed record MemberSlug(string Slug);

/// <summary>
/// A Roster instance's published members — what Events links a line-up to, and what a picker
/// in the admin lists. Scoped to the providing instance (the consumer's binding picks which).
/// </summary>
[DcmsContract("roster.members", 1, Description = "Published members of a Roster instance.")]
public interface IRosterMembers
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "Published members, newest first.")]
    Task<PagedResult<ContentItemDto>> ListAsync(MemberQuery input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "One published member by slug.")]
    Task<ContentItemDto?> GetBySlugAsync(MemberSlug input, CancellationToken ct);
}

/// <summary>Published content only, through <c>dcms.content@1</c>: drafts never leave the Roster.</summary>
public sealed class RosterMembers(IPluginContext context) : IRosterMembers
{
    public Task<PagedResult<ContentItemDto>> ListAsync(MemberQuery input, CancellationToken ct) =>
        context.Contracts.Get<IPluginContent>().ListAsync(new ContentListRequest("member", input.Page, input.PageSize), ct);

    public Task<ContentItemDto?> GetBySlugAsync(MemberSlug input, CancellationToken ct) =>
        context.Contracts.Get<IPluginContent>().GetBySlugAsync(new ContentLookup("member", input.Slug), ct);
}
