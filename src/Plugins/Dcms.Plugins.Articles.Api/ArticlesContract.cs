using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Articles.Api;

/// <summary>The fields of a published <c>article</c>, as authored.</summary>
public sealed record Article(string Title, string? Summary, string Body, Guid? HeroImage, Guid? Related, IReadOnlyList<string>? Tags);

[ContractEvent("articles.article.published")]
public sealed record ArticlePublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("articles.article.unpublished")]
public sealed record ArticleUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Published articles of an Articles instance. Drafts never leave the plugin.</summary>
[DcmsContract("articles.items", 1, Description = "Published articles of an Articles instance.",
    Events = [typeof(ArticlePublished), typeof(ArticleUnpublished)])]
public interface IArticles
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "Published articles, newest first.")]
    Task<ContentPage<Article>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "One published article by slug.")]
    Task<ContentEntry<Article>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}
