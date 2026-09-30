using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.Instagram.Api;

namespace Dcms.Plugins.Instagram;

internal sealed class InstagramPostsSource(IPluginContext context)
    : PublishedContentSource<InstagramPost>(context, "instagram-post"), IInstagramPosts
{
    public new Task<ContentPage<InstagramPost>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<InstagramPost>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}

internal sealed class InstagramReelsSource(IPluginContext context)
    : PublishedContentSource<InstagramReel>(context, "instagram-reel"), IInstagramReels
{
    public new Task<ContentPage<InstagramReel>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<InstagramReel>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
