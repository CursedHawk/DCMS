using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.FileDownloads.Api;

namespace Dcms.Plugins.FileDownloads;

internal sealed class DownloadFilesSource(IPluginContext context)
    : PublishedContentSource<DownloadFile>(context, "file"), IDownloadFiles
{
    public new Task<ContentPage<DownloadFile>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<DownloadFile>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
