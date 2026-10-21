using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Plugins.YouTube.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.YouTube;

/// <summary>
/// A YouTube channel's (or playlist's) latest videos, served from the tenant's own site.
///
/// <para>The Instagram plugin's shape without its machinery. Videos are synced into ordinary
/// <c>youtube-video</c> content, so the delivery API, cache, search, builder palette and typed
/// site client come with the content type. But public channel data needs no OAuth, so there
/// are no tokens to store or refresh, and YouTube's thumbnail URLs do not expire, so nothing is
/// mirrored: the admin pastes a link, and one platform API key reads it.</para>
/// </summary>
public sealed class YouTubePlugin : IPlugin
{
    public const string PluginId = "youtube";
    public const string VideoType = "youtube-video";

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: PluginId,
        name: "YouTube",
        description: "The latest videos from a YouTube channel or playlist, served through this site's API and ready to embed.",
        allowMultipleInstances: true,
        configJsonSchema: YouTubeSettings.Schema,
        permissions:
        [
            new PermissionDefinition("sync", "Sync YouTube videos",
                "Check a channel link and pull its latest videos now rather than at the next scheduled pass."),
        ],
        contentTypes:
        [
            new ContentTypeDefinition(
                Name: VideoType,
                Fields:
                [
                    // Title first: the console titles an item by its first text field.
                    new ContentFieldDefinition("title", ContentFieldType.Text, Required: false, "The video's title."),
                    new ContentFieldDefinition("videoId", ContentFieldType.Text, Required: true, "The video's id on YouTube. Also the item slug."),
                    new ContentFieldDefinition("description", ContentFieldType.Text, Required: false, "The video's description."),
                    new ContentFieldDefinition("url", ContentFieldType.Text, Required: false, "The video's page on YouTube."),
                    new ContentFieldDefinition("embedUrl", ContentFieldType.Text, Required: false,
                        "The privacy-enhanced player (youtube-nocookie.com), ready for an iframe."),
                    // Text, because it is YouTube's URL and not a DCMS asset; the Image reference
                    // tells the builders it is a picture, so Mode D can bind an Image to it.
                    new ContentFieldDefinition("thumbnail", ContentFieldType.Text, Required: false, "YouTube's largest still of the video.",
                        new ContentReferenceTarget(null, null, MediaCategory.Image)),
                    new ContentFieldDefinition("publishedAt", ContentFieldType.DateTime, Required: false, "When it was published on YouTube."),
                    new ContentFieldDefinition("durationSeconds", ContentFieldType.Number, Required: false, "Length in seconds."),
                    new ContentFieldDefinition("viewCount", ContentFieldType.Number, Required: false, "Views, as of the last sync."),
                    new ContentFieldDefinition("likeCount", ContentFieldType.Number, Required: false, "Likes, as of the last sync. Absent when the channel hides them."),
                    new ContentFieldDefinition("kind", ContentFieldType.Text, Required: false, "video, short or live (a stream, premiere or its recording)."),
                    new ContentFieldDefinition("channelTitle", ContentFieldType.Text, Required: false, "The channel that published it."),
                ],
                Searchable: true,
                SlugField: "videoId",
                Published: typeof(YouTubeVideoPublished),
                Unpublished: typeof(YouTubeVideoUnpublished)),
        ],
        publicConfigKeys: [YouTubeSettings.ChannelTitleKey],
        provides: [ContractProvision.Of<IYouTubeVideos, YouTubeVideosSource>()],
        consumes:
        [
            ContractRequirement.Of<IPluginContent>(),
            // A link that stopped resolving is reported through the bell.
            ContractRequirement.Of<IPluginNotifications>(),
        ],
        jobs: [JobDeclaration.Of<YouTubeSyncJob>("sync", TimeSpan.FromMinutes(30))],
        category: "Integrations",
        summary: "Shows a YouTube channel's or playlist's latest videos.",
        iconName: "Youtube",
        adminScreens: [AdminScreen.Settings]);

    public void ConfigureServices(IServiceCollection services, PluginHost host)
    {
        // The key, the sync and the job all live on the admin plane; sites read synced content.
        if (!host.IsAdmin) return;
        services.Configure<YouTubeOptions>(host.Configuration.GetSection(YouTubeOptions.SectionName));
        services.AddHttpClient<YouTubeClient>(client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<YouTubeSync>();
    }

    public void MapAdminEndpoints(IPluginEndpointBuilder endpoints)
    {
        // What a pasted link resolves to, so the admin sees the right channel before saving.
        endpoints.MapGet("/resolve", async (string? channel, string? playlist, YouTubeSync sync, YouTubeClient youtube, CancellationToken ct) =>
            {
                if (!youtube.IsConfigured)
                {
                    return Results.Problem(title: "YouTube is not configured on this deployment.",
                        statusCode: StatusCodes.Status501NotImplemented);
                }
                var (c, p, problem) = await sync.PreviewAsync(channel, playlist, ct);
                return Results.Ok(new { channel = c, playlist = p, error = problem });
            })
            .RequirePluginPermission("sync");

        // The same code path as the timer, run now, answering with what it did.
        endpoints.MapPost("/sync", async (IPluginContext context, YouTubeSync sync, CancellationToken ct) =>
            {
                var outcome = await sync.SyncAsync(context.Instance!, ct);
                return outcome.Ok
                    ? Results.Ok(new { created = outcome.Created, updated = outcome.Updated, removed = outcome.Removed })
                    : Results.BadRequest(new { error = outcome.Error });
            })
            .RequirePluginPermission("sync")
            .AuditAs("sync.requested");
    }
}
