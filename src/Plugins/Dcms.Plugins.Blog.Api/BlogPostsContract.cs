using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Blog.Api;

/// <summary>The fields of a published <c>post</c>, as authored.</summary>
public sealed record BlogPost(string Title, string? Excerpt, string Body, Guid? CoverImage, IReadOnlyList<string>? Tags);

[ContractEvent("blog.post.published")]
public sealed record BlogPostPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("blog.post.unpublished")]
public sealed record BlogPostUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Published posts of a Blog instance. Drafts never leave the plugin.</summary>
[DcmsContract("blog.posts", 1, Description = "Published posts of a Blog instance.",
    Events = [typeof(BlogPostPublished), typeof(BlogPostUnpublished)])]
public interface IBlogPosts
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "Published posts, newest first.")]
    Task<ContentPage<BlogPost>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "One published post by slug.")]
    Task<ContentEntry<BlogPost>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}
