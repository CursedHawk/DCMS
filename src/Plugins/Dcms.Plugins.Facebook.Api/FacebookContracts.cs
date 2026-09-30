using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Facebook.Api;

/// <summary>A mirrored post as the feed stores it. <c>Media</c>/<c>Thumbnail</c> are tenant media ids;
/// <c>MediaUrl</c> is set only when the file was not mirrored (video, by choice).</summary>
public sealed record FacebookPost(
    string? ExternalId,
    string? Permalink,
    string? Caption,
    Guid? Media,
    Guid? Thumbnail,
    string? MediaType,
    string? MediaUrl,
    string? PostedAt,
    string? Username,
    JsonElement? Children);

[ContractEvent("facebook.post.published")]
public sealed record FacebookPostPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("facebook.post.unpublished")]
public sealed record FacebookPostUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Posts mirrored from a connected Facebook Page.</summary>
[DcmsContract("facebook.posts", 1, Description = "Posts mirrored from a connected Facebook Page.", Events = [typeof(FacebookPostPublished), typeof(FacebookPostUnpublished)])]
public interface IFacebookPosts
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        ReturnsExternalText = true, Description = "Mirrored posts, newest first.")]
    Task<ContentPage<FacebookPost>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        ReturnsExternalText = true, Description = "One mirrored post by its Meta id.")]
    Task<ContentEntry<FacebookPost>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}
