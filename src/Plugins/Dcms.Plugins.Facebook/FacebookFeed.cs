using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.Facebook.Api;

namespace Dcms.Plugins.Facebook;

internal sealed class FacebookPostsSource(IPluginContext context)
    : PublishedContentSource<FacebookPost>(context, "facebook-post"), IFacebookPosts
{
    public new Task<ContentPage<FacebookPost>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<FacebookPost>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
