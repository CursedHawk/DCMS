using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.Articles.Api;

namespace Dcms.Plugins.Articles;

internal sealed class ArticlesSource(IPluginContext context)
    : PublishedContentSource<Article>(context, "article"), IArticles
{
    public new Task<ContentPage<Article>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<Article>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
