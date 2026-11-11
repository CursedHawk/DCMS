using Dcms.Plugins.YouTube.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.YouTube;

/// <summary><c>youtube.sync@1</c>: the settings screen's resolve and sync-now, through the same <see cref="YouTubeSync"/>. Admin plane only.</summary>
public sealed class YouTubeSyncing(IPluginContext context, IServiceProvider services) : IYouTubeSyncing
{
    private YouTubeSync Sync => services.GetService<YouTubeSync>()
        ?? throw new InvalidOperationException("YouTube syncs on the admin plane only.");

    public async Task<YouTubeSourcePreview> ResolveAsync(YouTubeSourceQuery input, CancellationToken ct)
    {
        EnsureConfigured();
        var (channel, playlist, problem) = await Sync.PreviewAsync(input.Channel, input.Playlist, ct);
        return new YouTubeSourcePreview(channel?.Id, channel?.Title, playlist?.Id, playlist?.Title, problem);
    }

    public async Task<YouTubeSyncResult> SyncNowAsync(CancellationToken ct)
    {
        EnsureConfigured();
        var outcome = await Sync.SyncAsync(context.Instance ?? throw new ContractValidationException("youtube.sync@1 is served by a YouTube instance."), ct);
        return outcome.Ok
            ? new YouTubeSyncResult(outcome.Created, outcome.Updated, outcome.Removed)
            : throw new ContractValidationException(outcome.Error!);
    }

    private void EnsureConfigured()
    {
        if (services.GetService<YouTubeClient>() is not { IsConfigured: true })
        {
            throw new ContractValidationException("YouTube is not configured on this deployment.");
        }
    }
}
