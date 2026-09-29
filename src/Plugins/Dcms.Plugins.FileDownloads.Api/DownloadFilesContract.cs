using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.FileDownloads.Api;

/// <summary>The fields of a published <c>file</c>, as authored.</summary>
public sealed record DownloadFile(string Title, string? Description, Guid? Asset, string? Category);

[ContractEvent("file-downloads.file.published")]
public sealed record DownloadFilePublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("file-downloads.file.unpublished")]
public sealed record DownloadFileUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Published downloads of a File downloads instance. Drafts never leave the plugin.</summary>
[DcmsContract("file-downloads.files", 1, Description = "Published downloads of a File downloads instance.",
    Events = [typeof(DownloadFilePublished), typeof(DownloadFileUnpublished)])]
public interface IDownloadFiles
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "Published files, newest first.")]
    Task<ContentPage<DownloadFile>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "One published file by slug.")]
    Task<ContentEntry<DownloadFile>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}
