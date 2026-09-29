using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.Blog.Api;

namespace Dcms.Plugins.Blog;

internal sealed class BlogPostsSource(IPluginContext context)
    : PublishedContentSource<BlogPost>(context, "post"), IBlogPosts
{
    public new Task<ContentPage<BlogPost>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<BlogPost>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
