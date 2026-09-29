using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.Events.Api;

namespace Dcms.Plugins.Events;

internal sealed class GigsSource(IPluginContext context)
    : PublishedContentSource<Gig>(context, "gig"), IGigs
{
    public new Task<ContentPage<Gig>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<Gig>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
