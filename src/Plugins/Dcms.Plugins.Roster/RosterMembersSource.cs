using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.Roster.Api;

namespace Dcms.Plugins.Roster;

internal sealed class RosterMembersSource(IPluginContext context)
    : PublishedContentSource<RosterMember>(context, "member"), IRosterMembers
{
    public new Task<ContentPage<RosterMember>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<RosterMember>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
