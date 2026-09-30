using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Instagram.Api;

/// <summary>A mirrored post as the feed stores it. <c>Media</c>/<c>Thumbnail</c> are tenant media ids;
/// <c>MediaUrl</c> is set only when the file was not mirrored (video, by choice).</summary>
public sealed record InstagramPost(
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

[ContractEvent("instagram.post.published")]
public sealed record InstagramPostPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("instagram.post.unpublished")]
public sealed record InstagramPostUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Posts mirrored from a connected Instagram account.</summary>
[DcmsContract("instagram.posts", 1, Description = "Posts mirrored from a connected Instagram account.", Events = [typeof(InstagramPostPublished), typeof(InstagramPostUnpublished)])]
public interface IInstagramPosts
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        ReturnsExternalText = true, Description = "Mirrored posts, newest first.")]
    Task<ContentPage<InstagramPost>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        ReturnsExternalText = true, Description = "One mirrored post by its Meta id.")]
    Task<ContentEntry<InstagramPost>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}

/// <summary>A mirrored post as the feed stores it. <c>Media</c>/<c>Thumbnail</c> are tenant media ids;
/// <c>MediaUrl</c> is set only when the file was not mirrored (video, by choice).</summary>
public sealed record InstagramReel(
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

[ContractEvent("instagram.reel.published")]
public sealed record InstagramReelPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("instagram.reel.unpublished")]
public sealed record InstagramReelUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Reels mirrored from a connected Instagram account.</summary>
[DcmsContract("instagram.reels", 1, Description = "Reels mirrored from a connected Instagram account.", Events = [typeof(InstagramReelPublished), typeof(InstagramReelUnpublished)])]
public interface IInstagramReels
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        ReturnsExternalText = true, Description = "Mirrored reels, newest first.")]
    Task<ContentPage<InstagramReel>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        ReturnsExternalText = true, Description = "One mirrored reel by its Meta id.")]
    Task<ContentEntry<InstagramReel>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}
