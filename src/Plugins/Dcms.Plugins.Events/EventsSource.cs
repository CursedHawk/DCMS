using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.Events.Api;

namespace Dcms.Plugins.Events;

internal sealed class EventsSource(IPluginContext context)
    : PublishedContentSource<Event>(context, "event"), IEvents
{
    public new Task<ContentPage<Event>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<Event>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
